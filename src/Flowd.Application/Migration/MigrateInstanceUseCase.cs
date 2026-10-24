using Flowd.Application.Deployment;
using Flowd.Application.Execution;
using Flowd.Engine.Results;

namespace Flowd.Application.Migration;

/// <summary>
/// Moves a running instance to another version of its process (FR-OPS-004). Every element the instance is waiting on (and every boundary
/// trigger it holds) must exist in the target version, with the same element kind for running elements, either under the same id or
/// through the explicit mapping. Nothing is changed when validation fails.
/// </summary>
public sealed class MigrateInstanceUseCase(IInstanceMigrationStore store, IProcessDefinitionStore definitions, IProcessModelProvider models, TimeProvider time)
{
    public async Task<Result<MigrationResult>> ExecuteAsync(string tenantId, long instanceKey, long targetDefinitionKey, IReadOnlyDictionary<string, string>? mapping, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetSnapshotAsync(tenantId, instanceKey, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new Failure(new NotFound($"Process instance {instanceKey} not found."));
        }

        var target = await definitions.GetAsync(tenantId, targetDefinitionKey, cancellationToken).ConfigureAwait(false);
        if (target is null || !string.Equals(target.BpmnProcessId, snapshot.BpmnProcessId, StringComparison.Ordinal))
        {
            return new Failure(new ValidationFailure("The target must be a deployed version of the same process."));
        }

        if (snapshot.State != "ACTIVE" || snapshot.DefinitionKey == targetDefinitionKey)
        {
            return new Failure(new Conflict("INSTANCE_NOT_MIGRATABLE", "Only an active instance that is not already on the target version can be migrated."));
        }

        var effective = Effective(snapshot, mapping);
        var problems = await ValidateAsync(tenantId, snapshot, targetDefinitionKey, effective, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new Failure(new ValidationFailure("The instance cannot be migrated.", [.. problems]));
        }

        return await store.MigrateAsync(tenantId, instanceKey, snapshot.DefinitionKey, targetDefinitionKey, effective, time.GetUtcNow(), cancellationToken).ConfigureAwait(false) switch
        {
            MigrationStoreOutcome.Migrated => new MigrationResult(instanceKey, snapshot.DefinitionKey, targetDefinitionKey),
            MigrationStoreOutcome.NotFound => new Failure(new NotFound($"Process instance {instanceKey} not found.")),
            _ => new Failure(new Conflict("INSTANCE_NOT_MIGRATABLE", "The instance changed while it was being migrated; try again.")), // non-union: enum
        };
    }

    private static Dictionary<string, string> Effective(MigrationSnapshot snapshot, IReadOnlyDictionary<string, string>? mapping)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in snapshot.ReferencedElementIds.Concat(snapshot.LiveElementIds).Distinct(StringComparer.Ordinal))
        {
            result[id] = mapping is not null && mapping.TryGetValue(id, out var mapped) ? mapped : id;
        }

        return result;
    }

    private async Task<List<FailureDetail>> ValidateAsync(string tenantId, MigrationSnapshot snapshot, long targetKey, Dictionary<string, string> effective, CancellationToken cancellationToken)
    {
        var problems = new List<FailureDetail>();
        var source = await models.GetGraphAsync(tenantId, snapshot.DefinitionKey, cancellationToken).ConfigureAwait(false);
        var target = await models.GetGraphAsync(tenantId, targetKey, cancellationToken).ConfigureAwait(false);
        if (source is null || target is null)
        {
            problems.Add(new FailureDetail(null, "MODEL_UNAVAILABLE", "The process models could not be loaded."));
            return problems;
        }

        var sourceNodes = source.Process.AllNodes().ToDictionary(n => n.Id, StringComparer.Ordinal);
        var targetNodes = target.Process.AllNodes().ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var (from, to) in effective)
        {
            if (!targetNodes.TryGetValue(to, out var targetNode))
            {
                problems.Add(new FailureDetail(from, "TARGET_ELEMENT_MISSING", $"Element '{from}' maps to '{to}', which does not exist in the target version."));
            }
            else if (snapshot.LiveElementIds.Contains(from, StringComparer.Ordinal) && sourceNodes.TryGetValue(from, out var sourceNode) && sourceNode.GetType() != targetNode.GetType())
            {
                problems.Add(new FailureDetail(from, "ELEMENT_KIND_CHANGED", $"Element '{from}' is a {sourceNode.GetType().Name} but '{to}' is a {targetNode.GetType().Name}."));
            }
        }

        return problems;
    }
}

/// <summary>A migrated instance: from which definition key to which.</summary>
public sealed record MigrationResult(long InstanceKey, long FromDefinitionKey, long ToDefinitionKey);
