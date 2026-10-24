namespace Flowd.Application.Readiness;

/// <summary>Outcome of a readiness check (API-013).</summary>
/// <param name="IsReady">True when the database is reachable and migrations are current.</param>
/// <param name="Detail">Short human-readable reason; never contains secrets.</param>
public sealed record ReadinessStatus(bool IsReady, string Detail);

/// <summary>Port: is this node able to serve traffic? (API-013, FR-DB-001).</summary>
public interface IReadinessProbe
{
    Task<ReadinessStatus> CheckAsync(CancellationToken cancellationToken);
}
