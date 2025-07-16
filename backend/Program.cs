using System.Text.Json;
using PaymentBackend.Models;
using PaymentBackend.Services;
using PaymentBackend.Workers;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization for AOT
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PaymentJsonSerializerContext.Default);
});

// Configure HTTP client with connection pooling for limited resources
builder.Services.AddHttpClient<PaymentProcessorService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8); // Reduzir para 8s
    client.DefaultRequestHeaders.Add("User-Agent", "PaymentBackend/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    MaxConnectionsPerServer = 50, // Reduzir para 50 devido às limitações de recursos
    ConnectTimeout = TimeSpan.FromSeconds(5),
    ResponseDrainTimeout = TimeSpan.FromSeconds(3) // Reduzir para 3s
});

// Register services
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var connectionString = builder.Configuration["REDIS_CONNECTION_STRING"] ?? "localhost:6379";
    var logger = provider.GetRequiredService<ILogger<RedisService>>();
    return new RedisService(connectionString, logger);
});

builder.Services.AddSingleton<IPaymentProcessorService, PaymentProcessorService>();
builder.Services.AddHostedService<PaymentWorker>();

// Configure Kestrel for performance with resource constraints
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
    serverOptions.AllowSynchronousIO = false;
    serverOptions.Limits.MaxRequestBodySize = 2048;
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(300);
    serverOptions.Limits.MaxConcurrentConnections = 500; // Reduzir para 500 devido ao limite de 350MB
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50; // Reduzir para 50
    serverOptions.Limits.MaxRequestHeadersTotalSize = 32768; // 32KB limit
    serverOptions.Limits.MaxRequestLineSize = 8192; // 8KB limit
});

var app = builder.Build();

app.Logger.LogInformation("Starting PaymentBackend application");

// Add lightweight request logging middleware
app.Use(async (context, next) =>
{
    var startTime = DateTime.UtcNow;
    
    // Log apenas erros e requests lentos para economizar recursos
    try
    {
        await next();
        
        var duration = DateTime.UtcNow - startTime;
        
        // Log apenas requests que demoram mais de 1 segundo
        if (duration.TotalMilliseconds > 1000)
        {
            app.Logger.LogWarning("Slow request: {Method} {Path} took {Duration}ms", 
                context.Request.Method, context.Request.Path, duration.TotalMilliseconds);
        }
    }
    catch (Exception ex)
    {
        var duration = DateTime.UtcNow - startTime;
        app.Logger.LogError(ex, "Request failed: {Method} {Path} in {Duration}ms", 
            context.Request.Method, context.Request.Path, duration.TotalMilliseconds);
        throw;
    }
});

// Configure request pipeline for performance
app.UseRouting();

// POST /payments endpoint - optimized for performance
app.MapPost("/payments", async (PaymentRequest request, IRedisService redisService) =>
{
    // Validate request (minimal logging)
    if (request.CorrelationId == Guid.Empty)
    {
        return Results.BadRequest(new { error = "CorrelationId is required" });
    }

    if (request.Amount <= 0)
    {
        return Results.BadRequest(new { error = "Amount must be greater than zero" });
    }

    try
    {
        await redisService.EnqueuePaymentAsync(request);
        return Results.Accepted();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to enqueue payment {CorrelationId}", request.CorrelationId);
        return Results.StatusCode(500);
    }
})
.WithName("CreatePayment");

// GET /payments-summary endpoint - optimized with validation
app.MapGet("/payments-summary", async (IRedisService redisService, IPaymentProcessorService processorService, string? from, string? to) =>
{
    try
    {
        DateTime? fromDate = null;
        DateTime? toDate = null;

        if (!string.IsNullOrEmpty(from))
        {
            if (!DateTime.TryParse(from, out var parsedFrom))
            {
                return Results.BadRequest(new { error = "Invalid 'from' date format. Use ISO8601 format." });
            }
            fromDate = parsedFrom.ToUniversalTime();
        }

        if (!string.IsNullOrEmpty(to))
        {
            if (!DateTime.TryParse(to, out var parsedTo))
            {
                return Results.BadRequest(new { error = "Invalid 'to' date format. Use ISO8601 format." });
            }
            toDate = parsedTo.ToUniversalTime();
        }

        // Use validated summary that compares with processor data
        var summary = await redisService.GetValidatedPaymentSummaryAsync(fromDate, toDate, processorService);
        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to get payment summary");
        return Results.StatusCode(500);
    }
})
.WithName("GetPaymentSummary");

// Health check endpoint
app.MapGet("/health", async (IRedisService redisService, IPaymentProcessorService processorService) =>
{
    try
    {
        // Test Redis connectivity
        await redisService.TrySetHealthCheckLockAsync();
        
        // Check if at least one processor is available (optional - for more detailed health)
        var hasAvailableProcessor = processorService.IsProcessorAvailable("default") || 
                                   processorService.IsProcessorAvailable("fallback");
        
        var healthResponse = new Dictionary<string, object>
        {
            ["status"] = "healthy",
            ["timestamp"] = DateTime.UtcNow,
            ["checks"] = new Dictionary<string, object>
            {
                ["redis"] = new Dictionary<string, object>
                {
                    ["status"] = "healthy",
                    ["message"] = "Connected"
                },
                ["processors"] = new Dictionary<string, object>
                {
                    ["status"] = hasAvailableProcessor ? "healthy" : "degraded",
                    ["message"] = hasAvailableProcessor ? "At least one processor available" : "All processors circuit breakers open"
                }
            }
        };
        
        return Results.Json(healthResponse, PaymentJsonSerializerContext.Default.DictionaryStringObject);
    }
    catch (Exception ex)
    {
        var errorResponse = new Dictionary<string, object>
        {
            ["status"] = "unhealthy",
            ["timestamp"] = DateTime.UtcNow,
            ["checks"] = new Dictionary<string, object>
            {
                ["redis"] = new Dictionary<string, object>
                {
                    ["status"] = "unhealthy",
                    ["message"] = ex.Message
                }
            }
        };
        
        return Results.Json(errorResponse, PaymentJsonSerializerContext.Default.DictionaryStringObject, statusCode: 503);
    }
})
.WithName("HealthCheck");

// Payment consistency verification endpoint
app.MapGet("/payments/{correlationId:guid}/verify", async (Guid correlationId, IRedisService redisService, IPaymentProcessorService processorService) =>
{
    try
    {
        var isConsistent = await redisService.VerifyPaymentConsistencyAsync(correlationId, processorService);
        
        return Results.Ok(new { 
            correlationId = correlationId,
            isConsistent = isConsistent,
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to verify payment consistency for {CorrelationId}", correlationId);
        return Results.StatusCode(500);
    }
})
.WithName("VerifyPaymentConsistency");

app.Logger.LogInformation("PaymentBackend application configured and starting...");

app.Run();

// Make the implicit Program class available for tests
public partial class Program { }
