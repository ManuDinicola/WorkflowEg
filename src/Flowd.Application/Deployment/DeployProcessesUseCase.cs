using System.Security.Cryptography;
using Flowd.Application.Timers;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;

namespace Flowd.Application.Deployment;

/// <summary>Parse, validate and store BPMN resources, all-or-nothing (FR-DEP-001, FR-DEP-002, FR-DEP-004, FR-SEC-004).</summary>
public sealed class DeployProcessesUseCase(IProcessDefinitionStore store, IStartTriggerStore startTriggers, IKeyGenerator keys, TimeProvider time)
{
    public const int MaxResourceBytes = 2 * 1024 * 1024;

    public Task<Result<IReadOnlyList<DeployedDefinition>>> ExecuteAsync(IReadOnlyList<DeploymentResource> resources, string tenantId, CancellationToken cancellationToken) =>
        ExecuteAsync(resources, tenantId, null, cancellationToken);

    /// <summary>Like the plain overload, but every process in the resources must be in <paramref name="allowedProcessIds"/> (null: no limit, FR-SEC-006).</summary>
    public async Task<Result<IReadOnlyList<DeployedDefinition>>> ExecuteAsync(
        IReadOnlyList<DeploymentResource> resources,
        string tenantId,
        IReadOnlySet<string>? allowedProcessIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            return new Failure(new ValidationFailure("A deployment needs at least one resource."));
        }

        var now = time.GetUtcNow();
        var drafts = new List<DefinitionDraft>();
        var plans = new Dictionary<string, StartTriggerPlan>(StringComparer.Ordinal);
        var details = new List<FailureDetail>();
        foreach (var resource in resources)
        {
            CollectDrafts(resource, tenantId, now, drafts, plans, details);
            RejectForbidden(resource.Name, drafts, allowedProcessIds, details);
        }

        if (details.Count > 0)
        {
            return new Failure(new ValidationFailure("The deployment contains invalid resources.", details));
        }

        var deployed = await store.DeployAsync(drafts, cancellationToken).ConfigureAwait(false);
        foreach (var definition in deployed.Where(d => d.IsNewVersion))
        {
            var plan = plans[definition.Definition.BpmnProcessId];
            await startTriggers.ReplaceAsync(tenantId, definition.Definition.BpmnProcessId, definition.Definition.Key, plan.Timers, plan.Messages, cancellationToken).ConfigureAwait(false);
        }

        return deployed;
    }

    private static void RejectForbidden(string resourceName, List<DefinitionDraft> drafts, IReadOnlySet<string>? allowed, List<FailureDetail> details)
    {
        if (allowed is null)
        {
            return;
        }

        foreach (var draft in drafts.Where(d => string.Equals(d.ResourceName, resourceName, StringComparison.Ordinal) && !allowed.Contains(d.BpmnProcessId)))
        {
            details.Add(new FailureDetail(resourceName, "PROCESS_NOT_ALLOWED", $"This API key may not deploy process '{draft.BpmnProcessId}'."));
        }
    }

    private void CollectDrafts(DeploymentResource resource, string tenantId, DateTimeOffset now, List<DefinitionDraft> drafts, Dictionary<string, StartTriggerPlan> plans, List<FailureDetail> details)
    {
        if (resource.Content.Length > MaxResourceBytes)
        {
            details.Add(new FailureDetail(resource.Name, "RESOURCE_TOO_LARGE", $"Resource exceeds {MaxResourceBytes} bytes."));
            return;
        }

        using var stream = new MemoryStream(resource.Content);
        switch (BpmnReader.Read(stream))
        {
            case BpmnInvalid invalid:
                details.AddRange(invalid.Issues.Select(i => new FailureDetail(i.ElementId is null ? resource.Name : $"{resource.Name}#{i.ElementId}", i.Code, i.Message)));
                break;
            case BpmnDefinitions definitions:
                var problems = definitions.Processes.SelectMany(p => ExpressionChecker.Check(resource.Name, p)).ToList();
                foreach (var process in definitions.Processes)
                {
                    plans[process.Id!] = StartTriggerPlanner.Plan(resource.Name, process, now, keys, problems);
                }

                if (problems.Count > 0)
                {
                    details.AddRange(problems);
                    break;
                }

                var checksum = SHA256.HashData(resource.Content);
                drafts.AddRange(definitions.Processes.Select(p =>
                    new DefinitionDraft(keys.NextKey(), tenantId, p.Id!, resource.Name, resource.Content, checksum, now)));
                break;
        }
    }
}
