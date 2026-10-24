namespace Flowd.Contracts.Deployments;

/// <summary>A process definition as exposed by the API; keys are strings (section 14).</summary>
public sealed record ProcessDefinitionDto(
    string Key,
    string BpmnProcessId,
    int Version,
    string ResourceName,
    DateTimeOffset DeployedAt);

/// <summary>One deployed process: <see cref="IsNewVersion"/> is false when identical XML was already deployed (FR-DEP-002).</summary>
public sealed record DeployedProcessDto(ProcessDefinitionDto Definition, bool IsNewVersion);

/// <summary>Response of <c>POST /v1/deployments</c> (API-001).</summary>
public sealed record DeploymentResponse(IReadOnlyList<DeployedProcessDto> Processes);

/// <summary>Cursor page (API-R2).</summary>
public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
