namespace Flowd.Application.Security;

/// <summary>A stored response replayed for a repeated <c>Idempotency-Key</c> (API-R1).</summary>
public sealed record IdempotentResponse(byte[] RequestHash, int Status, string? ContentType, byte[] Body, DateTimeOffset CreatedAt);

/// <summary>Port: remembers responses of mutating requests for 24 hours (API-R1).</summary>
public interface IIdempotencyStore
{
    Task<IdempotentResponse?> FindAsync(string scopedKey, CancellationToken cancellationToken);

    /// <summary>Stores the response unless another request stored one first.</summary>
    Task SaveAsync(string scopedKey, IdempotentResponse response, CancellationToken cancellationToken);
}
