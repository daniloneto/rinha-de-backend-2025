using System.Text.Json.Serialization;

[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentDb.Summary))]
[JsonSerializable(typeof(PaymentDb.ProcessorSummary))]
[JsonSerializable(typeof(PaymentWorker.HealthState))]
[JsonSerializable(typeof(ProcessorPaymentRequest))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
