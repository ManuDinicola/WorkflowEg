using Flowd.Bpmn.Model;

namespace Flowd.Bpmn.Reading;

/// <summary>Structural validation that collects every problem instead of stopping at the first (FR-DEP-004).</summary>
public static class BpmnValidator
{
    public static IReadOnlyList<BpmnIssue> Validate(BpmnDefinitions definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var issues = new List<BpmnIssue>();
        if (definitions.Processes.Count == 0)
        {
            issues.Add(new BpmnIssue(null, BpmnIssueCodes.NoProcess, "The file contains no bpmn:process."));
        }

        CheckProcessIds(definitions, issues);
        CheckUniqueElementIds(definitions, issues);
        foreach (var process in definitions.Processes)
        {
            if (!process.IsExecutable)
            {
                issues.Add(new BpmnIssue(process.Id, BpmnIssueCodes.NotExecutable, "Only executable processes (isExecutable=\"true\") can be deployed."));
            }

            ValidateScope(process.Id, process.Nodes, process.Flows, issues);
        }

        return issues;
    }

    private static void CheckProcessIds(BpmnDefinitions definitions, List<BpmnIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in definitions.Processes)
        {
            if (string.IsNullOrWhiteSpace(process.Id))
            {
                issues.Add(new BpmnIssue(null, BpmnIssueCodes.MissingProcessId, "A bpmn:process has no id."));
            }
            else if (!seen.Add(process.Id))
            {
                issues.Add(new BpmnIssue(process.Id, BpmnIssueCodes.DuplicateProcessId, $"Process id '{process.Id}' is used more than once."));
            }
        }
    }

    private static void CheckUniqueElementIds(BpmnDefinitions definitions, List<BpmnIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = definitions.Processes.SelectMany(p => p.AllNodes().Select(n => n.Id).Concat(p.AllFlows().Select(f => f.Id)));
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(new BpmnIssue(null, BpmnIssueCodes.MissingId, "An element has no id."));
            }
            else if (!seen.Add(id))
            {
                issues.Add(new BpmnIssue(id, BpmnIssueCodes.DuplicateId, $"Element id '{id}' is not unique."));
            }
        }
    }

    /// <summary>Validates one scope (the process itself or one embedded sub-process) and recurses into nested scopes.</summary>
    private static void ValidateScope(string? scopeId, IReadOnlyList<FlowNode> nodes, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues)
    {
        if (!nodes.Any(n => n is StartEvent))
        {
            issues.Add(new BpmnIssue(scopeId, BpmnIssueCodes.NoStartEvent, $"'{scopeId}' has no (supported) start event."));
        }

        var knownIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ValidateNode(node, flows, issues);
            if (node is SubProcess sub)
            {
                ValidateScope(sub.Id, sub.Nodes, sub.Flows, issues);
            }
        }

        foreach (var flow in flows)
        {
            ValidateFlow(flow, knownIds, issues);
        }
    }

    private static void ValidateNode(FlowNode node, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues)
    {
        var outgoing = flows.Where(f => string.Equals(f.SourceRef, node.Id, StringComparison.Ordinal)).ToList();
        switch (node)
        {
            case UnsupportedNode unsupported:
                issues.Add(new BpmnIssue(node.Id, BpmnIssueCodes.UnsupportedElement, $"Element '{node.Id}' of type '{unsupported.ElementType}' is not supported."));
                break;
            case ExclusiveGateway gateway:
                ValidateDiverging(gateway.Id, gateway.DefaultFlowId, outgoing, issues);
                break;
            case InclusiveGateway gateway:
                ValidateDiverging(gateway.Id, gateway.DefaultFlowId, outgoing, issues);
                break;
            case ServiceTask task:
                ValidateServiceTask(task, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case SubProcess sub:
                ValidateMappings(sub.Id, sub.Inputs.Concat(sub.Outputs), issues);
                RejectConditions(node, outgoing, issues);
                break;
            case ScriptTask script:
                ValidateScript(script, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case ParallelGateway or StartEvent or EndEvent or ManualTask:
                RejectConditions(node, outgoing, issues);
                break;
        }
    }

    private static void ValidateServiceTask(ServiceTask task, List<BpmnIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(task.TaskType))
        {
            issues.Add(new BpmnIssue(task.Id, BpmnIssueCodes.MissingTaskType, $"Task '{task.Id}' needs <flowd:taskDefinition type=\"...\"/>."));
        }

        ValidateMappings(task.Id, task.Inputs.Concat(task.Outputs), issues);
    }

    private static void ValidateScript(ScriptTask script, List<BpmnIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(script.Expression) || string.IsNullOrWhiteSpace(script.ResultVariable))
        {
            issues.Add(new BpmnIssue(script.Id, BpmnIssueCodes.InvalidMapping, $"Script task '{script.Id}' needs <flowd:script expression=\"...\" resultVariable=\"...\"/>."));
        }
        else if (script.ResultVariable.StartsWith("_flowd", StringComparison.Ordinal))
        {
            issues.Add(new BpmnIssue(script.Id, BpmnIssueCodes.InvalidMapping, $"Result variable '{script.ResultVariable}' uses the reserved prefix '_flowd' (FR-VAR-003)."));
        }
    }

    private static void ValidateMappings(string ownerId, IEnumerable<IoMapping> mappings, List<BpmnIssue> issues)
    {
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Target) || string.IsNullOrWhiteSpace(mapping.Source))
            {
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidMapping, $"A mapping on '{ownerId}' needs both source and target."));
            }
            else if (mapping.Target.StartsWith("_flowd", StringComparison.Ordinal))
            {
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidMapping, $"Mapping target '{mapping.Target}' uses the reserved prefix '_flowd' (FR-VAR-003)."));
            }
        }
    }

    private static void ValidateDiverging(string gatewayId, string? defaultFlowId, List<SequenceFlow> outgoing, List<BpmnIssue> issues)
    {
        if (defaultFlowId is { } defaultId && !outgoing.Any(f => string.Equals(f.Id, defaultId, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(gatewayId, BpmnIssueCodes.InvalidDefaultFlow, $"Default flow '{defaultId}' is not an outgoing flow of gateway '{gatewayId}'."));
        }

        if (outgoing.Count <= 1)
        {
            return;
        }

        foreach (var flow in outgoing.Where(f => string.IsNullOrWhiteSpace(f.Condition) && !string.Equals(f.Id, defaultFlowId, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.MissingCondition, $"Outgoing flow '{flow.Id}' of gateway '{gatewayId}' needs a condition or must be the default flow."));
        }
    }

    private static void RejectConditions(FlowNode node, List<SequenceFlow> outgoing, List<BpmnIssue> issues)
    {
        foreach (var flow in outgoing.Where(f => !string.IsNullOrWhiteSpace(f.Condition)))
        {
            issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.ConditionNotAllowed, $"Flow '{flow.Id}' leaves '{node.Id}', which does not support conditions (FR-EXEC-002)."));
        }
    }

    private static void ValidateFlow(SequenceFlow flow, HashSet<string> knownIds, List<BpmnIssue> issues)
    {
        if (!knownIds.Contains(flow.SourceRef))
        {
            issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.DanglingReference, $"Flow '{flow.Id}' has unknown sourceRef '{flow.SourceRef}'."));
        }

        if (!knownIds.Contains(flow.TargetRef))
        {
            issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.DanglingReference, $"Flow '{flow.Id}' has unknown targetRef '{flow.TargetRef}'."));
        }
    }
}
