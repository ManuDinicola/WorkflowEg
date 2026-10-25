namespace Flowd.Application.Deployment;

/// <summary>Port: persistence of process definitions (FR-DEP-001..003, FR-DEP-005, API-002).</summary>
public interface IProcessDefinitionStore
{
    /// <summary>
    /// Stores all drafts atomically (all-or-nothing). Per <c>(tenant, bpmnProcessId)</c>: when the checksum equals the latest
    /// version's, that version is returned unchanged; otherwise <c>latest + 1</c> is stored (FR-DEP-002).
    /// </summary>
    Task<IReadOnlyList<DeployedDefinition>> DeployAsync(IReadOnlyList<DefinitionDraft> drafts, CancellationToken cancellationToken);

    Task<ProcessDefinitionInfo?> GetAsync(string tenantId, long key, CancellationToken cancellationToken);

    /// <summary>The definition to start from: the given version, or the latest when <paramref name="version"/> is null (FR-DEP-003).</summary>
    Task<ProcessDefinitionInfo?> FindAsync(string tenantId, string bpmnProcessId, int? version, CancellationToken cancellationToken);

    /// <summary>The newest version of the process that was deployed with this version tag, or null (FR-DEP-007).</summary>
    Task<ProcessDefinitionInfo?> FindByVersionTagAsync(string tenantId, string bpmnProcessId, string versionTag, CancellationToken cancellationToken);

    /// <summary>The original, unmodified XML bytes (FR-DEP-005).</summary>
    Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken);

    Task<DefinitionPage> ListAsync(string tenantId, int pageSize, long? afterKey, CancellationToken cancellationToken);

    /// <summary>Deletes one version when no instance (running or finished) refers to it; its start triggers go with it (FR-DEP-006).</summary>
    Task<DeleteDefinitionOutcome> DeleteAsync(string tenantId, long key, CancellationToken cancellationToken);
}
