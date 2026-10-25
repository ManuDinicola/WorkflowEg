using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;
using Flowd.Expressions.Dmn;
using Flowd.Expressions.Values;

namespace Flowd.Application.Decisions;

/// <summary>Parse and store DMN resources, all-or-nothing (FR-DMN-003).</summary>
public sealed class DeployDecisionsUseCase(IDecisionStore store, IKeyGenerator keys, TimeProvider time)
{
    public async Task<Result<IReadOnlyList<DeployedDecision>>> ExecuteAsync(IReadOnlyList<DeploymentResource> resources, string tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            return new Failure(new ValidationFailure("A deployment needs at least one resource."));
        }

        var drafts = new List<DecisionDraft>();
        var details = new List<FailureDetail>();
        foreach (var resource in resources)
        {
            Collect(resource, tenantId, drafts, details);
        }

        if (details.Count > 0)
        {
            return new Failure(new ValidationFailure("The deployment contains invalid resources.", details));
        }

        IReadOnlyList<DeployedDecision> deployed = await store.DeployAsync(drafts, cancellationToken).ConfigureAwait(false);
        return deployed;
    }

    private void Collect(DeploymentResource resource, string tenantId, List<DecisionDraft> drafts, List<FailureDetail> details)
    {
        if (resource.Content.Length > DeployProcessesUseCase.MaxResourceBytes)
        {
            details.Add(new FailureDetail(resource.Name, "RESOURCE_TOO_LARGE", "Resource is too large."));
            return;
        }

        using var stream = new MemoryStream(resource.Content);
        switch (DmnParser.Read(stream))
        {
            case DmnInvalid invalid:
                details.Add(new FailureDetail(resource.Name, invalid.Code, invalid.Message));
                break;
            case DmnDefinitions definitions:
                var checksum = SHA256.HashData(resource.Content);
                drafts.AddRange(definitions.Decisions.Select(d => new DecisionDraft(keys.NextKey(), tenantId, d.Id, resource.Name, resource.Content, checksum, time.GetUtcNow())));
                break;
        }
    }
}

/// <summary>Evaluates the latest version of a stored decision with the given variables and records the evaluation (FR-DMN-003, FR-DMN-007).</summary>
public sealed class EvaluateDecisionUseCase(IDecisionStore store, IDecisionHistory history, IKeyGenerator keys, TimeProvider time)
{
    private const int MaxRecordedInputChars = 65_536;

    public Task<Result<DecisionEvaluation>> ExecuteAsync(string tenantId, string decisionId, JsonObject? variables, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, decisionId, variables, EvaluationSource.Api, cancellationToken);

    public async Task<Result<DecisionEvaluation>> ExecuteAsync(string tenantId, string decisionId, JsonObject? variables, EvaluationSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var info = await store.FindAsync(tenantId, decisionId, cancellationToken).ConfigureAwait(false);
        var bytes = info is null ? null : await store.GetResourceAsync(tenantId, info.Key, cancellationToken).ConfigureAwait(false);
        if (info is null || bytes is null)
        {
            return new Failure(new NotFound($"Decision '{decisionId}' not found."));
        }

        using var stream = new MemoryStream(bytes);
        if (DmnParser.Read(stream) is not DmnDefinitions definitions || definitions.Decisions.FirstOrDefault(d => d.Id == decisionId) is not { } decision)
        {
            return new Failure(new NotFound($"Decision '{decisionId}' not found."));
        }

        var started = time.GetTimestamp();
        var outcome = DecisionEvaluator.Evaluate(decision, definitions, ExpressionJson.ToVariables(variables ?? []), time.GetUtcNow());
        var elapsed = time.GetElapsedTime(started);
        var output = outcome is DecisionResult done ? ExpressionJson.ToJson(done.Value) : null;
        var error = outcome is DecisionFailure failed ? $"{failed.Code}: {failed.Message}" : null;
        await RecordAsync(tenantId, decisionId, info.Key, source, (int)elapsed.TotalMilliseconds, variables ?? [], output, error, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            DecisionResult => new DecisionEvaluation(output),
            DecisionFailure failure => new Failure(new ValidationFailure(failure.Message, [new FailureDetail(decisionId, failure.Code, failure.Message)])),
        };
    }

    private async Task RecordAsync(string tenantId, string decisionId, long decisionKey, EvaluationSource source, int durationMs, JsonObject input, JsonNode? output, string? error, CancellationToken cancellationToken)
    {
        var text = input.ToJsonString();
        JsonNode recorded = text.Length <= MaxRecordedInputChars ? input.DeepClone() : new JsonObject { ["_truncated"] = true, ["characters"] = text.Length };
        await history.AppendAsync(new DecisionEvaluationRecord(keys.NextKey(), tenantId, decisionId, decisionKey, time.GetUtcNow(), source.Kind, source.InstanceKey, durationMs, recorded, output?.DeepClone(), error), cancellationToken).ConfigureAwait(false);
    }
}
