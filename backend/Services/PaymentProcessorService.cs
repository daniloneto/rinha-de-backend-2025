using System.Text.Json;
using PaymentBackend.Models;

namespace PaymentBackend.Services;

public interface IPaymentProcessorService
{
    Task<bool> IsDefaultHealthyAsync();
    Task<(bool Success, string? ErrorMessage)> SendPaymentAsync(PaymentRequest payment, string processor);
    Task<bool> VerifyPaymentStatusAsync(Guid correlationId, string processor);
    Task<PaymentSummaryResponse> GetProcessorSummaryAsync(string processor, DateTime? from, DateTime? to);
    bool IsProcessorAvailable(string processor);
    Task<ProcessorHealthInfo?> GetProcessorHealthAsync(string processor);
    string SelectBestProcessor();
}

public sealed class PaymentProcessorService : IPaymentProcessorService
{
    private readonly HttpClient _httpClient;
    private readonly string _defaultUrl;
    private readonly string _fallbackUrl;
    private readonly ILogger<PaymentProcessorService> _logger;
      // Circuit breaker state
    private readonly Dictionary<string, CircuitBreakerState> _circuitBreakerStates = new();
    private readonly Dictionary<string, ProcessorHealthInfo> _healthCache = new();    private readonly object _circuitBreakerLock = new();
    private readonly object _healthCacheLock = new();    private const int FailureThreshold = 15; // Ajustar para 15 falhas (entre 10 e 20)
    private static readonly TimeSpan CircuitBreakerTimeout = TimeSpan.FromMinutes(1); // Reduzir para 1 minuto
    private static readonly TimeSpan HealthCacheTimeout = TimeSpan.FromSeconds(20); // Reduzir para 20 segundos

    private class CircuitBreakerState
    {
        public int FailureCount { get; set; }
        public DateTime LastFailureTime { get; set; }
        public bool IsOpen => FailureCount >= FailureThreshold && 
                             DateTime.UtcNow - LastFailureTime < CircuitBreakerTimeout;
    }    public PaymentProcessorService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PaymentProcessorService> logger)
    {
        _httpClient = httpClient;
        _defaultUrl = configuration["PAYMENT_PROCESSOR_URL_DEFAULT"] ?? "http://payment-processor-default:8080";
        _fallbackUrl = configuration["PAYMENT_PROCESSOR_URL_FALLBACK"] ?? "http://payment-processor-fallback:8080";
        _logger = logger;
        
        // Initialize circuit breaker states
        _circuitBreakerStates["default"] = new CircuitBreakerState();
        _circuitBreakerStates["fallback"] = new CircuitBreakerState();
        
        _logger.LogInformation("PaymentProcessorService initialized - Default: {DefaultUrl}, Fallback: {FallbackUrl}", 
            _defaultUrl, _fallbackUrl);
    }

    public bool IsProcessorAvailable(string processor)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakerStates.TryGetValue(processor, out var state))
            {
                var isAvailable = !state.IsOpen;
                //if (!isAvailable)
               // {
                    //_logger.LogDebug("Circuit breaker is open for {Processor} processor", processor);
               // }
                return isAvailable;
            }
            return true;
        }
    }

    private void RecordFailure(string processor)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakerStates.TryGetValue(processor, out var state))
            {
                state.FailureCount++;
                state.LastFailureTime = DateTime.UtcNow;
                
                //if (state.IsOpen)
               // {
                //    _logger.LogWarning("Circuit breaker opened for {Processor} processor after {FailureCount} failures", 
                //        processor, state.FailureCount);
               // }
            }
        }
    }

    private void RecordSuccess(string processor)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakerStates.TryGetValue(processor, out var state))
            {
                //if (state.FailureCount > 0)
                //{
                //    _logger.LogInformation("Circuit breaker reset for {Processor} processor", processor);
               // }
                state.FailureCount = 0;
            }
        }
    }    public async Task<bool> IsDefaultHealthyAsync()
    {
        _logger.LogDebug("Starting health check for default processor");
        
        // Check circuit breaker first - if it's open, don't even try health check
        if (!IsProcessorAvailable("default"))
        {
            //_logger.LogDebug("Default processor circuit breaker is open, skipping health check");
            return false;
        }        try
        {            
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4000)); // Reduzir para 4s
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_defaultUrl}/payments/service-health");
            request.Headers.TryAddWithoutValidation("X-Rinha-Token", "123");
            
            //_logger.LogDebug("Sending health check request to {Url}", $"{_defaultUrl}/payments/service-health");
            var response = await _httpClient.SendAsync(request, cts.Token);

            //_logger.LogInformation("Health check response - Status: {StatusCode}, Reason: {ReasonPhrase}", 
            //    response.StatusCode, response.ReasonPhrase);
            
            // Handle rate limiting - assume healthy if rate limited
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
               // _logger.LogWarning("Health check rate limited (429) - assuming healthy");
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                //_logger.LogWarning("Health check failed with status {StatusCode} - Content: {Content}", 
                //    response.StatusCode, errorContent);
                
                // Record failure for circuit breaker on non-success health check
                RecordFailure("default");
                return false;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            //_logger.LogDebug("Health check response content: {Content}", content);
            
            if (string.IsNullOrWhiteSpace(content))
            {
                //_logger.LogWarning("Health check returned empty content");
                RecordFailure("default");
                return false;
            }
            
            var healthCheck = JsonSerializer.Deserialize<HealthCheckResponse>(content, PaymentJsonSerializerContext.Default.HealthCheckResponse);
            
            if (healthCheck == null)
            {
                //_logger.LogWarning("Health check response could not be deserialized");
                RecordFailure("default");
                return false;
            }
            
            var isHealthy = !healthCheck.Failing; // If failing is false, processor is healthy
            //_logger.LogInformation("Default processor health check result: {IsHealthy} (Failing: {Failing}, MinResponseTime: {MinResponseTime}ms)", 
            //    isHealthy, healthCheck.Failing, healthCheck.MinResponseTime);

            // Record success for circuit breaker on successful health check
            if (isHealthy)
            {
                RecordSuccess("default");
            }
            else
            {
                RecordFailure("default");
            }
            
            return isHealthy;
        }
        catch (TaskCanceledException)
        {
            //if (ex.CancellationToken.IsCancellationRequested)
            //{
            //    _logger.LogWarning("Health check timed out after 4 seconds for default processor");
            //}
            //else
            //{
            //    _logger.LogWarning("Health check was cancelled for default processor");
            //}
            RecordFailure("default");
            return false;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Health check failed - invalid JSON response");
            RecordFailure("default");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed with exception");
            RecordFailure("default");
            return false;
        }
    }    public async Task<(bool Success, string? ErrorMessage)> SendPaymentAsync(PaymentRequest payment, string processor)
    {
        // Check circuit breaker first
        if (!IsProcessorAvailable(processor))
        {
            //_logger.LogWarning("Payment {CorrelationId} rejected - circuit breaker is open for {Processor}", 
            //    payment.CorrelationId, processor);
            return (false, $"Circuit breaker is open for {processor} processor");
        }

        var url = processor == "default" ? _defaultUrl : _fallbackUrl;
        
        //_logger.LogInformation("Attempting to send payment {CorrelationId} to {Processor} processor at {Url}", 
        //    payment.CorrelationId, processor, url);
        
        try
        {
            var processorRequest = new PaymentProcessorRequest(
                payment.CorrelationId,
                payment.Amount,
                DateTime.UtcNow
            );
            
            var json = JsonSerializer.Serialize(processorRequest, PaymentJsonSerializerContext.Default.PaymentProcessorRequest);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4000)); // Reduzir para 4s
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/payments");
            request.Content = content;
            request.Headers.TryAddWithoutValidation("X-Rinha-Token", "123");
            
            //_logger.LogDebug("Sending payment request: {Json}", json);
            
            var response = await _httpClient.SendAsync(request, cts.Token);
              //_logger.LogDebug("Payment response status: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                // Get response content to verify success
                var responseContent = await response.Content.ReadAsStringAsync();
                
                // Give the processor a moment to register the payment before verification
                await Task.Delay(100, cts.Token);
                
                // Verify the payment was actually processed
                var verificationSuccess = await VerifyPaymentStatusAsync(payment.CorrelationId, processor);
                
                if (verificationSuccess)
                {
                    RecordSuccess(processor);
                    _logger.LogInformation("Payment {CorrelationId} sent and verified successfully on {Processor}", 
                        payment.CorrelationId, processor);
                    return (true, null);
                }
                else
                {
                    RecordFailure(processor);
                    _logger.LogWarning("Payment {CorrelationId} was accepted by {Processor} but verification failed", 
                        payment.CorrelationId, processor);
                    return (false, "Payment was accepted but could not be verified");
                }
            }
            else
            {
                // Record failure for circuit breaker
                RecordFailure(processor);
                
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Payment {CorrelationId} failed to {Processor} with status {StatusCode}, content: {Content}",
                    payment.CorrelationId, processor, response.StatusCode, errorContent);
                return (false, $"HTTP {response.StatusCode}: {errorContent}");
            }
        }
        catch (TaskCanceledException)
        {
            RecordFailure(processor);
            //if (ex.CancellationToken.IsCancellationRequested)
            //{
            //_logger.LogWarning("Payment {CorrelationId} timed out to {Processor} after 4 seconds", 
            //    payment.CorrelationId, processor);
            //}
            //else
            //{
            //    _logger.LogWarning("Payment {CorrelationId} was cancelled to {Processor}",
            //        payment.CorrelationId, processor);
            // }
            return (false, "Request timed out");
        }
        catch (Exception ex)
        {
            RecordFailure(processor);
            _logger.LogError(ex, "Payment {CorrelationId} failed to {Processor} with exception", 
                payment.CorrelationId, processor);
            return (false, ex.Message);
        }
    }

    public async Task<ProcessorHealthInfo?> GetProcessorHealthAsync(string processor)
    {
        // Check if we have recent cached health info
        lock (_healthCacheLock)
        {
            if (_healthCache.TryGetValue(processor, out var cachedHealth) &&
                DateTime.UtcNow - cachedHealth.LastChecked < HealthCacheTimeout)
            {
                //_logger.LogDebug("Using cached health info for {Processor}: {IsHealthy}, ResponseTime: {ResponseTime}ms", 
                //           processor, cachedHealth.IsHealthy, cachedHealth.MinResponseTime);
                return cachedHealth;
            }
        }

        // Check circuit breaker first
        var isCircuitBreakerOpen = !IsProcessorAvailable(processor);
        if (isCircuitBreakerOpen)
        {
            var healthInfo = new ProcessorHealthInfo(false, int.MaxValue, DateTime.UtcNow, true);
            lock (_healthCacheLock)
            {
                _healthCache[processor] = healthInfo;
            }
            return healthInfo;
        }

        // Perform actual health check
        var url = processor == "default" ? _defaultUrl : _fallbackUrl;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4000));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/payments/service-health");
            request.Headers.TryAddWithoutValidation("X-Rinha-Token", "123");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await _httpClient.SendAsync(request, cts.Token);
            stopwatch.Stop();

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited - assume the last known good state or default to healthy
                lock (_healthCacheLock)
                {
                    if (_healthCache.TryGetValue(processor, out var lastKnown))
                    {
                        var updatedHealth = lastKnown with { LastChecked = DateTime.UtcNow };
                        _healthCache[processor] = updatedHealth;
                        return updatedHealth;
                    }
                }

                // No previous data, assume healthy but with high response time
                var healthInfo = new ProcessorHealthInfo(true, (int)stopwatch.ElapsedMilliseconds + 1000, DateTime.UtcNow, false);
                lock (_healthCacheLock)
                {
                    _healthCache[processor] = healthInfo;
                }
                return healthInfo;
            }

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(processor);
                var healthInfo = new ProcessorHealthInfo(false, int.MaxValue, DateTime.UtcNow, false);
                lock (_healthCacheLock)
                {
                    _healthCache[processor] = healthInfo;
                }
                return healthInfo;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(content))
            {
                RecordFailure(processor);
                var healthInfo = new ProcessorHealthInfo(false, int.MaxValue, DateTime.UtcNow, false);
                lock (_healthCacheLock)
                {
                    _healthCache[processor] = healthInfo;
                }
                return healthInfo;
            }

            var healthCheck = JsonSerializer.Deserialize<HealthCheckResponse>(content, PaymentJsonSerializerContext.Default.HealthCheckResponse);

            if (healthCheck == null)
            {
                RecordFailure(processor);
                var healthInfo = new ProcessorHealthInfo(false, int.MaxValue, DateTime.UtcNow, false);
                lock (_healthCacheLock)
                {
                    _healthCache[processor] = healthInfo;
                }
                return healthInfo;
            }

            var isHealthy = !healthCheck.Failing;
            var actualResponseTime = Math.Max((int)stopwatch.ElapsedMilliseconds, healthCheck.MinResponseTime);

            if (isHealthy)
            {
                RecordSuccess(processor);
            }
            else
            {
                RecordFailure(processor);
            }

            var result = new ProcessorHealthInfo(isHealthy, actualResponseTime, DateTime.UtcNow, false);
            lock (_healthCacheLock)
            {
                _healthCache[processor] = result;
            }

            //_logger.LogDebug("Health check for {Processor}: Healthy={IsHealthy}, MinResponseTime={MinResponseTime}ms, ActualTime={ActualTime}ms",
            //    processor, isHealthy, healthCheck.MinResponseTime, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception)
        {
            RecordFailure(processor);
            //_logger.LogWarning(ex, "Health check failed for {Processor}", processor);

            var healthInfo = new ProcessorHealthInfo(false, int.MaxValue, DateTime.UtcNow, false);
            lock (_healthCacheLock)
            {
                _healthCache[processor] = healthInfo;
            }
            return healthInfo;
        }
    }

    public string SelectBestProcessor()
    {
        // Get health info for both processors
        var defaultHealthTask = GetProcessorHealthAsync("default");
        var fallbackHealthTask = GetProcessorHealthAsync("fallback");
          // Wait for both (with timeout)
        Task.WaitAll(new[] { defaultHealthTask, fallbackHealthTask }, TimeSpan.FromSeconds(5)); // Aumentar de 3s para 5s
        
        var defaultHealth = defaultHealthTask.IsCompletedSuccessfully ? defaultHealthTask.Result : null;
        var fallbackHealth = fallbackHealthTask.IsCompletedSuccessfully ? fallbackHealthTask.Result : null;

        // Decision logic
        if (defaultHealth?.IsHealthy == true && fallbackHealth?.IsHealthy == true)
        {
            // Both healthy - choose the one with better response time
            var choice = defaultHealth.MinResponseTime <= fallbackHealth.MinResponseTime ? "default" : "fallback";
            //_logger.LogDebug("Both processors healthy - selected {Processor} (Default: {DefaultTime}ms, Fallback: {FallbackTime}ms)", 
            //    choice, defaultHealth.MinResponseTime, fallbackHealth.MinResponseTime);
            return choice;
        }
        
        if (defaultHealth?.IsHealthy == true)
        {
            //_logger.LogDebug("Selected default processor (fallback unhealthy)");
            return "default";
        }
        
        if (fallbackHealth?.IsHealthy == true)
        {
            //_logger.LogDebug("Selected fallback processor (default unhealthy)");
            return "fallback";
        }
          // Both unhealthy - prefer fallback as it's usually more stable
        //_logger.LogWarning("Both processors unhealthy - defaulting to fallback");
        return "fallback";
    }    public async Task<bool> VerifyPaymentStatusAsync(Guid correlationId, string processor)
    {
        var url = processor == "default" ? _defaultUrl : _fallbackUrl;
        
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000)); // Shorter timeout for verification
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/payments/{correlationId}");
            request.Headers.TryAddWithoutValidation("X-Rinha-Token", "123");
            
            _logger.LogDebug("Verifying payment {CorrelationId} on {Processor} at {Url}", correlationId, processor, $"{url}/payments/{correlationId}");
            
            var response = await _httpClient.SendAsync(request, cts.Token);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Payment {CorrelationId} not found on {Processor}", correlationId, processor);
                return false;
            }
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to verify payment {CorrelationId} on {Processor} - Status: {StatusCode}", 
                    correlationId, processor, response.StatusCode);
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Empty response when verifying payment {CorrelationId} on {Processor}", correlationId, processor);
                return false;
            }            // Try to parse the response - the processor should return payment details if it exists
            // The exact format may vary, but if we get a successful response with content, the payment exists
            try
            {
                using var document = JsonDocument.Parse(content);
                var paymentDetails = document.RootElement;
                
                // Check if the response contains the correlation ID (indicating the payment was found)
                if (paymentDetails.TryGetProperty("correlationId", out var idProperty) ||
                    paymentDetails.TryGetProperty("id", out idProperty))
                {
                    var returnedId = idProperty.GetString();
                    if (returnedId == correlationId.ToString())
                    {
                        _logger.LogDebug("Payment {CorrelationId} verified successfully on {Processor}", correlationId, processor);
                        return true;
                    }
                }
                
                // If we can't find the correlation ID in the response, but we got a 200 OK,
                // assume the payment exists (some processors might have different response formats)
                _logger.LogDebug("Payment {CorrelationId} found on {Processor} (format may vary)", correlationId, processor);
                return true;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse payment verification response for {CorrelationId} on {Processor}", correlationId, processor);
                // If we can't parse but got 200 OK, assume the payment exists
                return true;
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Payment verification timeout for {CorrelationId} on {Processor}", correlationId, processor);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify payment status for {CorrelationId} on {Processor}", correlationId, processor);
            return false;
        }
    }    public async Task<PaymentSummaryResponse> GetProcessorSummaryAsync(string processor, DateTime? from, DateTime? to)
    {
        var url = processor == "default" ? _defaultUrl : _fallbackUrl;
        
        try
        {
            var queryParams = new List<string>();
            if (from.HasValue)
                queryParams.Add($"from={from.Value:yyyy-MM-ddTHH:mm:ssZ}");
            if (to.HasValue)
                queryParams.Add($"to={to.Value:yyyy-MM-ddTHH:mm:ssZ}");
            
            var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
              using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
              // Use only the admin endpoint - this is the correct endpoint on processors
            var endpoint = $"{url}/admin/payments-summary{queryString}";
            _logger.LogInformation("Calling summary endpoint {Endpoint} for processor {Processor}", endpoint, processor);
            
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("X-Rinha-Token", "123");
                
                var response = await _httpClient.SendAsync(request, cts.Token);
                  if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation("Raw response from {Processor} at {Endpoint}: {Content}", processor, endpoint, content);
                      if (!string.IsNullOrWhiteSpace(content))
                    {
                        try
                        {
                            // Try first as ProcessorSingleSummary (individual processor response)
                            var singleSummary = JsonSerializer.Deserialize<ProcessorSingleSummary>(content, PaymentJsonSerializerContext.Default.ProcessorSingleSummary);
                            if (singleSummary != null)
                            {
                                _logger.LogInformation("Successfully parsed single summary from {Processor}: TotalRequests={TotalRequests}, TotalAmount={TotalAmount}, TotalFee={TotalFee}",
                                    processor, singleSummary.TotalRequests, singleSummary.TotalAmount, singleSummary.TotalFee);

                                // Create the appropriate response based on which processor this is
                                if (processor == "default")
                                {
                                    return new PaymentSummaryResponse(
                                        new ProcessorSummary(singleSummary.TotalRequests, singleSummary.TotalAmount),
                                        new ProcessorSummary(0, 0m) // This processor doesn't know about the other
                                    );
                                }
                                else // fallback
                                {
                                    return new PaymentSummaryResponse(
                                        new ProcessorSummary(0, 0m), // This processor doesn't know about the other
                                        new ProcessorSummary(singleSummary.TotalRequests, singleSummary.TotalAmount)
                                    );
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Deserialized single summary from {Processor} is null", processor);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, "Failed to deserialize summary response from {Processor}. Content: {Content}", processor, content);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Empty content received from {Processor} at {Endpoint}", processor, endpoint);
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("HTTP error from {Processor} at {Endpoint} - Status: {StatusCode}, Content: {ErrorContent}", 
                        processor, endpoint, response.StatusCode, errorContent);
                }
                
                _logger.LogWarning("Failed to get summary from {Processor} - Status: {StatusCode}", processor, response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get summary from {Processor} at {Endpoint}", processor, endpoint);
            }
            
            _logger.LogWarning("Could not retrieve summary from {Processor}, returning empty summary", processor);
            return new PaymentSummaryResponse(
                new ProcessorSummary(0, 0m),
                new ProcessorSummary(0, 0m)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get processor summary from {Processor}", processor);
            return new PaymentSummaryResponse(
                new ProcessorSummary(0, 0m),
                new ProcessorSummary(0, 0m)
            );
        }
    }
}
