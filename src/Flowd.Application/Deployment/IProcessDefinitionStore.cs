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

    /// <summary>The original, unmodified XML bytes (FR-DEP-005).</summary>
    Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken);

    Task<DefinitionPage> ListAsync(string tenantId, int pageSize, long? afterKey, CancellationToken cancellationToken);
}
