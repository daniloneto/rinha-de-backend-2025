using StackExchange.Redis;
using System.Text.Json;
using PaymentBackend.Models;

namespace PaymentBackend.Services;

public interface IRedisService
{
    Task EnqueuePaymentAsync(PaymentRequest payment);
    Task<PaymentRequest?> DequeuePaymentAsync();
    Task SavePaymentResultAsync(PaymentData paymentData);
    Task<PaymentSummaryResponse> GetPaymentSummaryAsync(DateTime? from, DateTime? to);
    Task<PaymentSummaryResponse> GetValidatedPaymentSummaryAsync(DateTime? from, DateTime? to, IPaymentProcessorService processorService);
    Task<bool> TrySetHealthCheckLockAsync();
    Task EnqueuePaymentWithDelayAsync(PaymentRequest payment, TimeSpan delay);
    Task MoveToDeadLetterQueueAsync(PaymentRequest payment);
    Task<bool> TryLockPaymentProcessingAsync(string correlationId, TimeSpan timeout);
    Task ReleaseLockPaymentProcessingAsync(string correlationId);
    Task<bool> HasPaymentBeenProcessedAsync(Guid correlationId);
    Task MarkPaymentAsProcessedAsync(Guid correlationId, string processor, decimal amount);
    Task<bool> VerifyPaymentConsistencyAsync(Guid correlationId, IPaymentProcessorService processorService);
}

public sealed class RedisService : IRedisService, IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _database;
    private readonly ILogger<RedisService> _logger;    private const string PaymentQueueKey = "payment_queue";
    private const string PaymentDataKey = "payment_data";
    private const string ProcessedPaymentsKey = "processed_payments";
    private const string HealthCheckLockKey = "health_check_lock";
    private const string DelayedPaymentQueueKey = "delayed_payment_queue";
    private const string DeadLetterQueueKey = "dead_letter_queue";
    private const int HealthCheckLockTimeoutSeconds = 5;

    public RedisService(string connectionString, ILogger<RedisService> logger)
    {
        _logger = logger;
        _logger.LogInformation("Connecting to Redis at {ConnectionString}", connectionString);
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _database = _redis.GetDatabase();
        _logger.LogInformation("Redis connection established");
    }    public async Task EnqueuePaymentAsync(PaymentRequest payment)
    {
        try
        {
            var json = JsonSerializer.Serialize(payment, PaymentJsonSerializerContext.Default.PaymentRequest);
            await _database.ListLeftPushAsync(PaymentQueueKey, json);
            //_logger.LogDebug("Payment {CorrelationId} enqueued to Redis", payment.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue payment {CorrelationId} to Redis", payment.CorrelationId);
            throw;
        }
    }    public async Task<PaymentRequest?> DequeuePaymentAsync()
    {
        try
        {
            // First check for delayed payments that are ready to process
            await ProcessDelayedPayments();
            
            var json = await _database.ListRightPopAsync(PaymentQueueKey);
            if (!json.HasValue)
            {                
                return null;
            }

            var payment = JsonSerializer.Deserialize<PaymentRequest>(json!, PaymentJsonSerializerContext.Default.PaymentRequest);
            //_logger.LogDebug("Payment {CorrelationId} dequeued from Redis", payment?.CorrelationId);
            return payment;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dequeue payment from Redis");
            throw;
        }
    }

    public async Task EnqueuePaymentWithDelayAsync(PaymentRequest payment, TimeSpan delay)
    {
        try
        {
            var executeAt = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeSeconds();
            var json = JsonSerializer.Serialize(payment, PaymentJsonSerializerContext.Default.PaymentRequest);
            
            await _database.SortedSetAddAsync(DelayedPaymentQueueKey, json, executeAt);
            //_logger.LogDebug("Payment {CorrelationId} scheduled for retry in {Delay}", 
            //   payment.CorrelationId, delay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue delayed payment {CorrelationId}", payment.CorrelationId);
            throw;
        }
    }

    public async Task MoveToDeadLetterQueueAsync(PaymentRequest payment)
    {
        try
        {
            var json = JsonSerializer.Serialize(payment, PaymentJsonSerializerContext.Default.PaymentRequest);
            await _database.ListLeftPushAsync(DeadLetterQueueKey, json);
            //_logger.LogWarning("Payment {CorrelationId} moved to dead letter queue after {RetryCount} retries", 
            //    payment.CorrelationId, payment.RetryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move payment {CorrelationId} to dead letter queue", payment.CorrelationId);
            throw;
        }
    }

    private async Task ProcessDelayedPayments()
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var readyPayments = await _database.SortedSetRangeByScoreAsync(
                DelayedPaymentQueueKey, 0, now, Exclude.None, Order.Ascending, 0, 10);

            foreach (var paymentJson in readyPayments)
            {
                // Remove from delayed queue and add to main queue
                var removed = await _database.SortedSetRemoveAsync(DelayedPaymentQueueKey, paymentJson);
                if (removed)
                {
                    await _database.ListLeftPushAsync(PaymentQueueKey, paymentJson);
                    
                    var payment = JsonSerializer.Deserialize<PaymentRequest>(paymentJson!, PaymentJsonSerializerContext.Default.PaymentRequest);
                    //_logger.LogDebug("Delayed payment {CorrelationId} moved to main queue", payment?.CorrelationId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process delayed payments");
        }
    }    public async Task SavePaymentResultAsync(PaymentData paymentData)
    {
        try
        {
            var json = JsonSerializer.Serialize(paymentData, PaymentJsonSerializerContext.Default.PaymentData);
            
            // Use atomic transaction to ensure consistency
            var transaction = _database.CreateTransaction();
              // Add to payment data list
            _ = transaction.ListLeftPushAsync(PaymentDataKey, json);
            
            // Mark as processed with expiration (24 hours)
            var processedKey = $"{ProcessedPaymentsKey}:{paymentData.CorrelationId}";
            _ = transaction.StringSetAsync(processedKey, $"{paymentData.Processor}:{paymentData.Amount}", TimeSpan.FromHours(24));
              // Execute transaction atomically
            var success = await transaction.ExecuteAsync();
            
            //_logger.LogDebug("Payment result {CorrelationId} saved to Redis", paymentData.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save payment result {CorrelationId} to Redis", paymentData.CorrelationId);
            throw;
        }
    }    public async Task<PaymentSummaryResponse> GetPaymentSummaryAsync(DateTime? from, DateTime? to)
    {
        try
        {
            //_logger.LogInformation("Getting payment summary from Redis - From: {From}, To: {To}", from, to);
            
            // Always read from the main list to ensure consistency
            var allData = await _database.ListRangeAsync(PaymentDataKey);
            _logger.LogDebug("Retrieved {Count} payment records from Redis", allData.Length);
            
            var defaultTotal = 0;
            var defaultAmountTotal = 0m;
            var fallbackTotal = 0;
            var fallbackAmountTotal = 0m;

            foreach (var item in allData)
            {
                if (!item.HasValue || string.IsNullOrWhiteSpace(item))
                    continue;

                try
                {
                    var paymentData = JsonSerializer.Deserialize<PaymentData>(item!, PaymentJsonSerializerContext.Default.PaymentData);
                    
                    if (paymentData == null) continue;

                    // Filter by date range if specified
                    if (from.HasValue && paymentData.Timestamp < from.Value) continue;
                    if (to.HasValue && paymentData.Timestamp > to.Value) continue;

                    if (paymentData.Processor == "default")
                    {
                        defaultTotal++;
                        defaultAmountTotal += paymentData.Amount;
                    }
                    else if (paymentData.Processor == "fallback")
                    {
                        fallbackTotal++;
                        fallbackAmountTotal += paymentData.Amount;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize payment data: {Data}", item.ToString());
                    continue;
                }
            }

            var summary = new PaymentSummaryResponse(
                new ProcessorSummary(defaultTotal, defaultAmountTotal),
                new ProcessorSummary(fallbackTotal, fallbackAmountTotal)
            );
            
            _logger.LogDebug("Payment summary calculated - Default: {DefaultCount}/{DefaultAmount}, Fallback: {FallbackCount}/{FallbackAmount}", 
                defaultTotal, defaultAmountTotal, fallbackTotal, fallbackAmountTotal);
            
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get payment summary from Redis");
            // Return a safe default instead of throwing
            return new PaymentSummaryResponse(
                new ProcessorSummary(0, 0m),
                new ProcessorSummary(0, 0m)
            );
        }
    }

    public async Task<bool> TrySetHealthCheckLockAsync()
    {
        try
        {
            var result = await _database.StringSetAsync(HealthCheckLockKey, "locked", TimeSpan.FromSeconds(HealthCheckLockTimeoutSeconds), When.NotExists);
            //_logger.LogDebug("Health check lock attempt result: {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set health check lock in Redis");
            return false;
        }
    }    public async Task<bool> TryLockPaymentProcessingAsync(string correlationId, TimeSpan timeout)
    {
        try
        {
            var lockKey = $"payment_lock:{correlationId}";
            var result = await _database.StringSetAsync(lockKey, "processing", timeout, When.NotExists);
            //_logger.LogDebug("Payment processing lock for {CorrelationId}: {Result}", correlationId, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire payment processing lock for {CorrelationId}", correlationId);
            return false;
        }
    }

    public async Task ReleaseLockPaymentProcessingAsync(string correlationId)
    {
        try
        {
            var lockKey = $"payment_lock:{correlationId}";
            await _database.KeyDeleteAsync(lockKey);
            //_logger.LogDebug("Payment processing lock released for {CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release payment processing lock for {CorrelationId}", correlationId);
        }
    }    public void Dispose()
    {
        //_logger.LogInformation("Disposing Redis connection");
        _redis?.Dispose();
    }

    public async Task<bool> HasPaymentBeenProcessedAsync(Guid correlationId)
    {
        try
        {
            var processedKey = $"{ProcessedPaymentsKey}:{correlationId}";
            var exists = await _database.KeyExistsAsync(processedKey);
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check if payment {CorrelationId} was processed", correlationId);
            return false;
        }
    }

    public async Task MarkPaymentAsProcessedAsync(Guid correlationId, string processor, decimal amount)
    {
        try
        {
            var processedKey = $"{ProcessedPaymentsKey}:{correlationId}";
            await _database.StringSetAsync(processedKey, $"{processor}:{amount}", TimeSpan.FromHours(24));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark payment {CorrelationId} as processed", correlationId);
            throw;
        }
    }    public async Task<PaymentSummaryResponse> GetValidatedPaymentSummaryAsync(DateTime? from, DateTime? to, IPaymentProcessorService processorService)
    {
        try
        {
            // Get local summary from Redis
            var localSummary = await GetPaymentSummaryAsync(from, to);
            if (localSummary?.Default == null || localSummary.Fallback == null)
            {
                _logger.LogWarning("Local summary returned null values, returning empty summary");
                return new PaymentSummaryResponse(
                    new ProcessorSummary(0, 0m),
                    new ProcessorSummary(0, 0m)
                );
            }
              // Get processor summaries for validation
            var defaultProcessorSummary = await processorService.GetProcessorSummaryAsync("default", from, to);
            var fallbackProcessorSummary = await processorService.GetProcessorSummaryAsync("fallback", from, to);
            
            // Extract the relevant processor data from each response
            var defaultProcessorData = defaultProcessorSummary?.Default ?? new ProcessorSummary(0, 0m);
            var fallbackProcessorData = fallbackProcessorSummary?.Fallback ?? new ProcessorSummary(0, 0m);
            
            // Compare and log discrepancies
            var defaultDiscrepancy = Math.Abs(localSummary.Default.TotalAmount - defaultProcessorData.TotalAmount);
            var fallbackDiscrepancy = Math.Abs(localSummary.Fallback.TotalAmount - fallbackProcessorData.TotalAmount);
            
            if (defaultDiscrepancy > 0.01m || fallbackDiscrepancy > 0.01m)
            {
                _logger.LogWarning("Payment summary discrepancy detected - Local vs Processor: Default ({LocalDefault} vs {ProcessorDefault}), Fallback ({LocalFallback} vs {ProcessorFallback})",
                    localSummary.Default.TotalAmount, defaultProcessorData.TotalAmount,
                    localSummary.Fallback.TotalAmount, fallbackProcessorData.TotalAmount);
                
                // Return processor values as they are the source of truth
                return new PaymentSummaryResponse(
                    defaultProcessorData,
                    fallbackProcessorData
                );
            }
            
            // If values match, return local summary
            return localSummary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get validated payment summary");
            // Fallback to local summary if processor validation fails
            try
            {
                return await GetPaymentSummaryAsync(from, to);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Failed to get fallback local summary");
                return new PaymentSummaryResponse(
                    new ProcessorSummary(0, 0m),
                    new ProcessorSummary(0, 0m)
                );
            }
        }
    }

    public async Task<bool> VerifyPaymentConsistencyAsync(Guid correlationId, IPaymentProcessorService processorService)
    {
        try
        {
            // Check if we have the payment locally
            var hasLocal = await HasPaymentBeenProcessedAsync(correlationId);
            
            if (!hasLocal)
            {
                _logger.LogDebug("Payment {CorrelationId} not found locally", correlationId);
                return false;
            }
            
            // Get the local payment data to know which processor was used
            var processedKey = $"{ProcessedPaymentsKey}:{correlationId}";
            var localData = await _database.StringGetAsync(processedKey);
            
            if (!localData.HasValue)
            {
                _logger.LogWarning("Payment {CorrelationId} marked as processed but no processor data found", correlationId);
                return false;
            }
            
            // Parse processor info (format: "processor:amount")
            var parts = localData.ToString().Split(':');
            if (parts.Length != 2)
            {
                _logger.LogWarning("Invalid processor data format for payment {CorrelationId}: {Data}", correlationId, localData);
                return false;
            }
            
            var processor = parts[0];
            
            // Verify with the actual processor
            var processorHasPayment = await processorService.VerifyPaymentStatusAsync(correlationId, processor);
            
            if (!processorHasPayment)
            {
                _logger.LogError("CONSISTENCY ERROR: Payment {CorrelationId} exists locally but not on {Processor}", 
                    correlationId, processor);
                return false;
            }
            
            _logger.LogDebug("Payment {CorrelationId} consistency verified with {Processor}", correlationId, processor);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify payment consistency for {CorrelationId}", correlationId);
            return false;
        }
    }
}
