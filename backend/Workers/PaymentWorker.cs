using PaymentBackend.Services;
using PaymentBackend.Models;

namespace PaymentBackend.Workers;

public sealed class PaymentWorker : BackgroundService
{
    private readonly IRedisService _redisService;
    private readonly IPaymentProcessorService _processorService;
    private readonly ILogger<PaymentWorker> _logger;
    private readonly SemaphoreSlim _healthCheckSemaphore = new(1, 1);
    private DateTime _lastHealthCheck = DateTime.MinValue;
      private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = {
        TimeSpan.FromSeconds(30),   // First retry after 30 seconds
        TimeSpan.FromMinutes(2),    // Second retry after 2 minutes
        TimeSpan.FromMinutes(5)     // Third retry after 5 minutes
    };

    public PaymentWorker(
        IRedisService redisService,
        IPaymentProcessorService processorService,
        ILogger<PaymentWorker> logger)
    {
        _redisService = redisService;
        _processorService = processorService;
        _logger = logger;
    }    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Payment worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {                var payment = await _redisService.DequeuePaymentAsync();
                if (payment == null)
                {                    
                    await Task.Delay(100, stoppingToken); // Aumentar de 25ms para 100ms
                    continue;
                }

                //_logger.LogInformation("Dequeued payment {CorrelationId} from queue", payment.CorrelationId);
                await ProcessPaymentAsync(payment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment");
                await Task.Delay(1000, stoppingToken); // Longer delay on error
            }
        }
        
        _logger.LogInformation("Payment worker stopped");
    }    private async Task ProcessPaymentAsync(PaymentRequest payment)
    {
        //_logger.LogInformation("Starting to process payment {CorrelationId} with amount {Amount} (retry {RetryCount})", 
        //payment.CorrelationId, payment.Amount, payment.RetryCount);

        // Check if payment was already processed
        var alreadyProcessed = await _redisService.HasPaymentBeenProcessedAsync(payment.CorrelationId);
        if (alreadyProcessed)
        {
            //_logger.LogInformation("Payment {CorrelationId} already processed, skipping", payment.CorrelationId);
            return;
        }

        // Try to acquire lock to prevent duplicate processing
        var lockAcquired = await _redisService.TryLockPaymentProcessingAsync(
            payment.CorrelationId.ToString(),
            TimeSpan.FromMinutes(5));

        if (!lockAcquired)
        {
          //  _logger.LogWarning("Payment {CorrelationId} is already being processed or completed, skipping",
           //     payment.CorrelationId);
            return;
        }

        try
        {
            // Double-check if payment was processed while acquiring lock
            alreadyProcessed = await _redisService.HasPaymentBeenProcessedAsync(payment.CorrelationId);
            if (alreadyProcessed)
            {
                //_logger.LogInformation("Payment {CorrelationId} was processed while acquiring lock, skipping", payment.CorrelationId);
                return;
            }

            // Use intelligent processor selection
            var processor = _processorService.SelectBestProcessor();

            //_logger.LogInformation("Selected {Processor} processor for payment {CorrelationId}",
            //    processor, payment.CorrelationId);

            // Try to send payment with confirmation
            var (success, errorMessage) = await _processorService.SendPaymentAsync(payment, processor);

            // If selected processor failed, try the other one (only if it's available)
            if (!success)
            {
                var alternativeProcessor = processor == "default" ? "fallback" : "default";

                if (_processorService.IsProcessorAvailable(alternativeProcessor))
                {
                    //_logger.LogWarning("{Processor} processor failed for payment {CorrelationId} ({ErrorMessage}), trying {AlternativeProcessor}",
                    //    processor, payment.CorrelationId, errorMessage, alternativeProcessor);

                    var (altSuccess, altErrorMessage) = await _processorService.SendPaymentAsync(payment, alternativeProcessor);
                    success = altSuccess;
                    processor = success ? alternativeProcessor : processor;
                    errorMessage = altSuccess ? null : altErrorMessage;
                }
                //else
                //{
                    //_logger.LogWarning("{Processor} processor failed for payment {CorrelationId} ({ErrorMessage}), but {AlternativeProcessor} is not available",
                    //    processor, payment.CorrelationId, errorMessage, alternativeProcessor);
                //}
            }

            if (success)
            {
                var paymentData = new PaymentData(
                    payment.CorrelationId,
                    payment.Amount,
                    processor,
                    DateTime.UtcNow
                );

                await _redisService.SavePaymentResultAsync(paymentData);

               //_logger.LogInformation("Payment {CorrelationId} processed successfully with {Processor}",
               //     payment.CorrelationId, processor);
            }
            else
            {
                _logger.LogError("Payment {CorrelationId} failed to process with both processors. Last error: {ErrorMessage}", 
                    payment.CorrelationId, errorMessage);
                await HandleFailedPaymentAsync(payment);
            }
        }
        finally
        {
            // Always release the lock
            await _redisService.ReleaseLockPaymentProcessingAsync(payment.CorrelationId.ToString());
        }
    }

    private async Task HandleFailedPaymentAsync(PaymentRequest payment)
    {
        _logger.LogError("Payment {CorrelationId} failed to process with both processors (retry {RetryCount})", 
            payment.CorrelationId, payment.RetryCount);

        // Check if we should retry
        if (payment.RetryCount < MaxRetries)
        {
            var retryDelay = RetryDelays[payment.RetryCount];
            var retryPayment = payment with 
            { 
                RetryCount = payment.RetryCount + 1, 
                LastRetryAt = DateTime.UtcNow 
            };

            //_logger.LogWarning("Scheduling payment {CorrelationId} for retry {RetryCount} in {Delay}", 
            //    payment.CorrelationId, retryPayment.RetryCount, retryDelay);

            await _redisService.EnqueuePaymentWithDelayAsync(retryPayment, retryDelay);
        }
        else
        {
            _logger.LogError("Payment {CorrelationId} exceeded maximum retries ({MaxRetries}), moving to dead letter queue", 
                payment.CorrelationId, MaxRetries);

            await _redisService.MoveToDeadLetterQueueAsync(payment);
        }
    }private async Task<bool> ShouldCheckHealthAsync()
    {
        if (!await _healthCheckSemaphore.WaitAsync(10))
        {
            //_logger.LogDebug("Health check semaphore busy, skipping health check");
            return false;
        }

        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastHealthCheck < TimeSpan.FromSeconds(5))
            {
               // _logger.LogDebug("Health check too recent (last check: {LastCheck}), skipping", _lastHealthCheck);
                return false;
            }

            // Try to acquire Redis lock
            if (!await _redisService.TrySetHealthCheckLockAsync())
            {
                //_logger.LogDebug("Could not acquire Redis health check lock, skipping");
                return false;
            }

            _lastHealthCheck = now;
            //_logger.LogDebug("Health check allowed - last check: {LastCheck}", _lastHealthCheck);
            return true;
        }
        finally
        {
            _healthCheckSemaphore.Release();
        }
    }

    public override void Dispose()
    {
        _healthCheckSemaphore?.Dispose();
        base.Dispose();
    }
}
