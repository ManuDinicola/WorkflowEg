namespace Flowd.Application.Deployment;

/// <summary>A BPMN file submitted for deployment.</summary>
public sealed record DeploymentResource(string Name, byte[] Content);

/// <summary>A process definition ready to be stored; the store assigns the version (FR-DEP-002).</summary>
public sealed record DefinitionDraft(
    long Key,
    string TenantId,
    string BpmnProcessId,
    string ResourceName,
    byte[] Resource,
    byte[] Checksum,
    DateTimeOffset DeployedAt,
    string? VersionTag = null);

/// <summary>Stored process definition metadata (the XML is fetched separately, FR-DEP-005).</summary>
public sealed record ProcessDefinitionInfo(
    long Key,
    string TenantId,
    string BpmnProcessId,
    int Version,
    string ResourceName,
    DateTimeOffset DeployedAt,
    string? VersionTag = null);

/// <summary>Outcome for one process of a deployment: new version, or the existing one when the checksum matched.</summary>
public sealed record DeployedDefinition(ProcessDefinitionInfo Definition, bool IsNewVersion);

/// <summary>A page of definitions; <see cref="NextCursor"/> is null on the last page (API-R2).</summary>
public sealed record DefinitionPage(IReadOnlyList<ProcessDefinitionInfo> Items, long? NextCursor);

/// <summary>Outcome of deleting a process definition on demand (FR-DEP-006).</summary>
public enum DeleteDefinitionOutcome
{
    Deleted,
    NotFound,
    InUse,
}
