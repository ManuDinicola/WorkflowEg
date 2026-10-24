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

/// <summary>Evaluates the latest version of a stored decision with the given variables (FR-DMN-003).</summary>
public sealed class EvaluateDecisionUseCase(IDecisionStore store, TimeProvider time)
{
    public async Task<Result<DecisionEvaluation>> ExecuteAsync(string tenantId, string decisionId, JsonObject? variables, CancellationToken cancellationToken)
    {
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

        var outcome = DecisionEvaluator.Evaluate(decision, ExpressionJson.ToVariables(variables ?? []), time.GetUtcNow());
        return outcome switch
        {
            DecisionResult result => new DecisionEvaluation(ExpressionJson.ToJson(result.Value)),
            DecisionFailure failure => new Failure(new ValidationFailure(failure.Message, [new FailureDetail(decisionId, failure.Code, failure.Message)])),
        };
    }
}
