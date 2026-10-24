namespace Flowd.Application.Decisions;

/// <summary>A decision ready to be stored; the store assigns the version.</summary>
public sealed record DecisionDraft(long Key, string TenantId, string DecisionId, string ResourceName, byte[] Resource, byte[] Checksum, DateTimeOffset DeployedAt);

/// <summary>Stored decision metadata.</summary>
public sealed record DecisionInfo(long Key, string TenantId, string DecisionId, int Version, string ResourceName, DateTimeOffset DeployedAt);

/// <summary>Outcome for one decision of a deployment: new version, or the existing one when the checksum matched.</summary>
public sealed record DeployedDecision(DecisionInfo Decision, bool IsNewVersion);

/// <summary>Port: persistence of DMN decisions (FR-DMN-003).</summary>
public interface IDecisionStore
{
    Task<IReadOnlyList<DeployedDecision>> DeployAsync(IReadOnlyList<DecisionDraft> drafts, CancellationToken cancellationToken);

    /// <summary>The latest version of a decision, or null.</summary>
    Task<DecisionInfo?> FindAsync(string tenantId, string decisionId, CancellationToken cancellationToken);

    Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken);

    Task<IReadOnlyList<DecisionInfo>> ListAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>The value a decision evaluated to, as JSON (null when the decision has no match).</summary>
public sealed record DecisionEvaluation(System.Text.Json.Nodes.JsonNode? Value);
