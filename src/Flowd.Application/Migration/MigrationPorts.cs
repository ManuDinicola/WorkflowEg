namespace Flowd.Application.Migration;

/// <summary>What the migration use case needs to know about a running instance (FR-OPS-004).</summary>
public sealed record MigrationSnapshot(long DefinitionKey, string BpmnProcessId, string State, IReadOnlyList<string> ReferencedElementIds, IReadOnlyList<string> LiveElementIds);

/// <summary>Outcome of the store part of a migration.</summary>
public enum MigrationStoreOutcome
{
    Migrated,
    NotFound,
    NotActive,
    DefinitionChanged,
}

/// <summary>Port: reads and rewrites the rows that tie a running instance to its process definition.</summary>
public interface IInstanceMigrationStore
{
    Task<MigrationSnapshot?> GetSnapshotAsync(string tenantId, long instanceKey, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: locks the instance, checks it is still active on <paramref name="expectedDefinitionKey"/>, points it to
    /// <paramref name="targetDefinitionKey"/> and renames the element ids of its element instances, jobs, timers, subscriptions and user tasks.
    /// </summary>
    Task<MigrationStoreOutcome> MigrateAsync(string tenantId, long instanceKey, long expectedDefinitionKey, long targetDefinitionKey, IReadOnlyDictionary<string, string> mapping, DateTimeOffset now, CancellationToken cancellationToken);
}
