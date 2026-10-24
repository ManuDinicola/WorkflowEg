using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Flowd.Application.Observability;
using Flowd.Infrastructure.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Flowd.Infrastructure.Background;

/// <summary>
/// Delivers outbox rows to the configured webhook with an HMAC-SHA256 signature (FR-HIS-002, FR-ARCH-005). Delivery is
/// at-least-once; every body carries a unique <c>eventId</c> so consumers can de-duplicate. Rows are leased with
/// <c>FOR UPDATE SKIP LOCKED</c>, so several nodes can run it (FR-ARCH-004).
/// </summary>
public sealed partial class OutboxPublisher(NpgsqlDataSource dataSource, HttpClient client, IOptions<FlowdOptions> options, TimeProvider time, ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const string SignatureHeader = "X-Flowd-Signature";
    public const string EventIdHeader = "X-Flowd-Event-Id";
    public const string EventTypeHeader = "X-Flowd-Event-Type";
    private const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private long _pending;

    public static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.WebhookUrl is null)
        {
            return; // no webhook configured: rows stay in the outbox for another publisher
        }

        EngineTelemetry.OutboxPending = () => Interlocked.Read(ref _pending);
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PublishBatchSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Delivers one batch. Returns how many events were accepted by the webhook.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var url = options.Value.WebhookUrl ?? throw new InvalidOperationException("No webhook URL is configured.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            "SELECT seq AS Seq, topic AS Topic, payload::text AS Payload FROM outbox WHERE published_at IS NULL ORDER BY seq LIMIT @batch FOR UPDATE SKIP LOCKED",
            new { batch = BatchSize },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var delivered = 0;
        foreach (var row in rows)
        {
            var error = await TryDeliverAsync(url, row, cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                await connection.ExecuteAsync(new CommandDefinition("UPDATE outbox SET published_at = @now, attempts = attempts + 1, last_error = NULL WHERE seq = @seq", new { now = time.GetUtcNow().UtcDateTime, row.Seq }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                delivered++;
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition("UPDATE outbox SET attempts = attempts + 1, last_error = @error WHERE seq = @seq", new { error, row.Seq }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            break; // keep the order: later events wait for this one
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _pending, await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM outbox WHERE published_at IS NULL", cancellationToken: cancellationToken)).ConfigureAwait(false));
        return delivered;
    }

    private async Task<string?> TryDeliverAsync(Uri url, OutboxRow row, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(row.Payload)!.AsObject();
        body["eventId"] = row.Seq.ToString(CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(EventIdHeader, row.Seq.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(EventTypeHeader, row.Topic);
        if (!string.IsNullOrEmpty(options.Value.WebhookSecret))
        {
            request.Headers.Add(SignatureHeader, Sign(options.Value.WebhookSecret, bytes));
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
    }

    private async Task PublishBatchSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await PublishBatchAsync(cancellationToken).ConfigureAwait(false) == BatchSize)
            {
                // a full batch means more is waiting
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox publishing failed; will retry.")]
    private partial void LogPublishFailed(Exception exception);

    private sealed class OutboxRow
    {
        public long Seq { get; set; }

        public string Topic { get; set; } = string.Empty;

        public string Payload { get; set; } = "{}";
    }
}
