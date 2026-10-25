namespace Flowd.Application.Security;

/// <summary>
/// A stored response replayed for a repeated <c>Idempotency-Key</c> (API-R1). <c>Status</c> 0 marks a request that is still running: the key is
/// reserved before the command runs, so a second request with the same key cannot run it again at the same time.
/// </summary>
public sealed record IdempotentResponse(byte[] RequestHash, int Status, string? ContentType, byte[] Body, DateTimeOffset CreatedAt)
{
    public const int InProgress = 0;

    public bool IsInProgress => Status == InProgress;
}

/// <summary>Port: remembers responses of mutating requests for 24 hours (API-R1).</summary>
public interface IIdempotencyStore
{
    Task<IdempotentResponse?> FindAsync(string scopedKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the key for a request that is about to run. Succeeds when there is no row, or when the row is a finished response older than
    /// <paramref name="retentionCutoff"/>, or a reservation older than <paramref name="staleCutoff"/> (its request died). False when another request holds the key.
    /// </summary>
    Task<bool> TryReserveAsync(string scopedKey, byte[] requestHash, DateTimeOffset now, DateTimeOffset retentionCutoff, DateTimeOffset staleCutoff, CancellationToken cancellationToken);

    /// <summary>Replaces the reservation with the final response.</summary>
    Task SaveAsync(string scopedKey, IdempotentResponse response, CancellationToken cancellationToken);

    /// <summary>Drops the reservation of a request that failed so that a retry can run (server errors are not remembered).</summary>
    Task ReleaseAsync(string scopedKey, CancellationToken cancellationToken);
}
