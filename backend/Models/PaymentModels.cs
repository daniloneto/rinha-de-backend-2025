using System.Text.Json.Serialization;

namespace PaymentBackend.Models;

[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentProcessorRequest))]
[JsonSerializable(typeof(PaymentSummaryResponse))]
[JsonSerializable(typeof(ProcessorSummary))]
[JsonSerializable(typeof(ProcessorSingleSummary))]
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSerializable(typeof(PaymentData))]
[JsonSerializable(typeof(ProcessorHealthInfo))]
[JsonSerializable(typeof(PaymentProcessorResponse))]
[JsonSerializable(typeof(PaymentStatusCheckResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public partial class PaymentJsonSerializerContext : JsonSerializerContext
{
}

public sealed record PaymentRequest(
    Guid CorrelationId,
    decimal Amount,
    int RetryCount = 0,
    DateTime? LastRetryAt = null
);

public sealed record PaymentProcessorRequest(
    Guid CorrelationId,
    decimal Amount,
    DateTime RequestedAt
);

public sealed record PaymentSummaryResponse(
    ProcessorSummary Default,
    ProcessorSummary Fallback
);

public sealed record ProcessorSummary(
    int TotalRequests,
    decimal TotalAmount
);

public sealed record ProcessorSingleSummary(
    [property: JsonPropertyName("totalAmount")] decimal TotalAmount,
    [property: JsonPropertyName("totalRequests")] int TotalRequests,
    [property: JsonPropertyName("totalFee")] decimal TotalFee,
    [property: JsonPropertyName("feePerTransaction")] decimal FeePerTransaction
);

public sealed record HealthCheckResponse(
    bool Failing,
    int MinResponseTime
);

public sealed record PaymentData(
    Guid CorrelationId,
    decimal Amount,
    string Processor,
    DateTime Timestamp
);

public sealed record ProcessorHealthInfo(
    bool IsHealthy,
    int MinResponseTime,
    DateTime LastChecked,
    bool IsCircuitBreakerOpen
);

public sealed record PaymentProcessorResponse(
    bool Success,
    string? ErrorMessage = null,
    string? TransactionId = null
);

public sealed record PaymentStatusCheckResponse(
    bool Found,
    bool Processed,
    string? Status = null,
    decimal? Amount = null
);
