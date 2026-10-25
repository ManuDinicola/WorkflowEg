using System.Text.Json.Nodes;

namespace Flowd.Application.Decisions;

/// <summary>Where an evaluation came from: a direct API call, or the business rule task of an instance.</summary>
public sealed record EvaluationSource(string Kind, long? InstanceKey)
{
    public static EvaluationSource Api { get; } = new("api", null);

    public static EvaluationSource Task(long instanceKey) => new("task", instanceKey);
}

/// <summary>One evaluation of a decision: what went in, what came out (or the error), when and how long it took (FR-DMN-007).</summary>
public sealed record DecisionEvaluationRecord(
    long Key,
    string TenantId,
    string DecisionId,
    long? DecisionKey,
    DateTimeOffset EvaluatedAt,
    string Source,
    long? InstanceKey,
    int DurationMs,
    JsonNode Input,
    JsonNode? Output,
    string? Error);

public sealed record DecisionEvaluationPage(IReadOnlyList<DecisionEvaluationRecord> Items, long? NextCursor);

/// <summary>Port: the history of decision evaluations.</summary>
public interface IDecisionHistory
{
    Task AppendAsync(DecisionEvaluationRecord record, CancellationToken cancellationToken);

    Task<DecisionEvaluationPage> ListAsync(string tenantId, string? decisionId, long? instanceKey, int pageSize, long? afterKey, CancellationToken cancellationToken);

    Task<DecisionEvaluationRecord?> GetAsync(string tenantId, long key, CancellationToken cancellationToken);
}
