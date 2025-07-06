using System;
using System.Data;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(Channel.CreateUnbounded<Payment>());
builder.Services.AddSingleton<PaymentDb>();
builder.Services.AddHostedService<PaymentWorker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

app.MapPost("/payments", async (PaymentRequest payment, Channel<Payment> channel) =>
{
    if (payment.CorrelationId == Guid.Empty || payment.Amount <= 0)
    {
        return Results.BadRequest();
    }

    await channel.Writer.WriteAsync(new Payment
    {
        CorrelationId = payment.CorrelationId,
        Amount = payment.Amount,
        RequestedAt = DateTime.UtcNow
    });

    return Results.Accepted();
});

app.MapGet("/payments-summary", (PaymentDb db, DateTime? from, DateTime? to) =>
{
    var summary = db.GetSummary(from, to);
    return Results.Json(summary, AppJsonContext.Default.Summary);
});

app.MapPost("/purge-payments", (PaymentDb db) =>
{
    db.Purge();
    return Results.Ok();
});

app.Run();

record PaymentRequest(Guid CorrelationId, decimal Amount);

record Payment
{
    public Guid CorrelationId { get; init; }
    public decimal Amount { get; init; }
    public DateTime RequestedAt { get; init; }
}

record ProcessedPayment
{
    public Guid CorrelationId { get; init; }
    public decimal Amount { get; init; }
    public string Processor { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

class PaymentDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public PaymentDb()
    {
        _conn = new SqliteConnection("Data Source=app.db");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS payments (CorrelationId TEXT PRIMARY KEY, Amount REAL, Processor TEXT, Timestamp TEXT)";
        cmd.ExecuteNonQuery();
    }

    public void Save(ProcessedPayment payment)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO payments (CorrelationId, Amount, Processor, Timestamp) VALUES (@id, @amt, @proc, @ts)";
        cmd.Parameters.AddWithValue("@id", payment.CorrelationId.ToString());
        cmd.Parameters.AddWithValue("@amt", payment.Amount);
        cmd.Parameters.AddWithValue("@proc", payment.Processor);
        cmd.Parameters.AddWithValue("@ts", payment.Timestamp.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public Summary GetSummary(DateTime? from, DateTime? to)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT Processor, COUNT(*), SUM(Amount) FROM payments WHERE (@from IS NULL OR Timestamp >= @from) AND (@to IS NULL OR Timestamp <= @to) GROUP BY Processor";
        cmd.Parameters.AddWithValue("@from", from?.ToString("O"));
        cmd.Parameters.AddWithValue("@to", to?.ToString("O"));
        using var reader = cmd.ExecuteReader();
        var result = new Summary();
        while (reader.Read())
        {
            var processor = reader.GetString(0);
            var count = reader.GetInt32(1);
            var amount = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
            if (processor == "default")
            {
                result.Default.TotalRequests = count;
                result.Default.TotalAmount = amount;
            }
            else
            {
                result.Fallback.TotalRequests = count;
                result.Fallback.TotalAmount = amount;
            }
        }
        return result;
    }

    public void Purge()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM payments";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    public class Summary
    {
        public ProcessorSummary Default { get; init; } = new();
        public ProcessorSummary Fallback { get; init; } = new();
    }

    public class ProcessorSummary
    {
        public int TotalRequests { get; set; }
        public decimal TotalAmount { get; set; }
    }
}

class PaymentWorker : BackgroundService
{
    private readonly Channel<Payment> _channel;
    private readonly PaymentDb _db;
    private readonly HttpClient _http = new();
    private readonly string _defaultUrl = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_DEFAULT") ?? "http://payment-processor-default:8080";
    private readonly string _fallbackUrl = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_URL_FALLBACK") ?? "http://payment-processor-fallback:8080";
    private DateTime _lastHealthCheck;
    private HealthState _defaultHealth = new();

    public PaymentWorker(Channel<Payment> channel, PaymentDb db)
    {
        _channel = channel;
        _db = db;
        _http.Timeout = TimeSpan.FromSeconds(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var payment in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await EnsureHealthAsync();
            string url;
            string proc;
            if (!_defaultHealth.Failing && _defaultHealth.MinResponseTime < 1000)
            {
                url = _defaultUrl;
                proc = "default";
            }
            else
            {
                url = _fallbackUrl;
                proc = "fallback";
            }

            if (!await TrySendAsync(url, payment))
            {
                if (proc == "default")
                {
                    if (!await TrySendAsync(_fallbackUrl, payment))
                    {
                        continue;
                    }
                    proc = "fallback";
                }
                else
                {
                    continue;
                }
            }

            _db.Save(new ProcessedPayment
            {
                CorrelationId = payment.CorrelationId,
                Amount = payment.Amount,
                Processor = proc,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private async Task<bool> TrySendAsync(string baseUrl, Payment payment)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var req = new
            {
                correlationId = payment.CorrelationId,
                amount = payment.Amount,
                requestedAt = payment.RequestedAt.ToString("O")
            };
            var res = await _http.PostAsJsonAsync($"{baseUrl}/payments", req, cts.Token);
            res.EnsureSuccessStatusCode();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureHealthAsync()
    {
        if (DateTime.UtcNow - _lastHealthCheck < TimeSpan.FromSeconds(5))
            return;
        _lastHealthCheck = DateTime.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var res = await _http.GetFromJsonAsync<HealthState>($"{_defaultUrl}/payments/service-health", cts.Token);
            if (res != null)
                _defaultHealth = res;
        }
        catch
        {
            _defaultHealth = new HealthState { Failing = true, MinResponseTime = int.MaxValue };
        }
    }

    class HealthState
    {
        public bool Failing { get; set; }
        public int MinResponseTime { get; set; }
    }
}
