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
            ValidateProcess(process, issues);
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
        var ids = definitions.Processes.SelectMany(p => p.Nodes.Select(n => n.Id).Concat(p.Flows.Select(f => f.Id)));
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

    private static void ValidateProcess(BpmnProcess process, List<BpmnIssue> issues)
    {
        if (!process.IsExecutable)
        {
            issues.Add(new BpmnIssue(process.Id, BpmnIssueCodes.NotExecutable, "Only executable processes (isExecutable=\"true\") can be deployed."));
        }

        if (!process.Nodes.Any(n => n is StartEvent))
        {
            issues.Add(new BpmnIssue(process.Id, BpmnIssueCodes.NoStartEvent, "The process has no (supported) start event."));
        }

        var knownIds = process.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in process.Nodes)
        {
            ValidateNode(node, process, issues);
        }

        foreach (var flow in process.Flows)
        {
            ValidateFlow(flow, knownIds, issues);
        }
    }

    private static void ValidateNode(FlowNode node, BpmnProcess process, List<BpmnIssue> issues)
    {
        var outgoing = process.Flows.Where(f => string.Equals(f.SourceRef, node.Id, StringComparison.Ordinal)).ToList();
        if (node is ServiceTask task)
        {
            ValidateMappings(task, issues);
        }

        switch (node)
        {
            case UnsupportedNode unsupported:
                issues.Add(new BpmnIssue(node.Id, BpmnIssueCodes.UnsupportedElement, $"Element '{node.Id}' of type '{unsupported.ElementType}' is not supported."));
                break;
            case ExclusiveGateway gateway:
                ValidateExclusiveGateway(gateway, outgoing, issues);
                break;
            case ServiceTask { TaskType: null or "" }:
                issues.Add(new BpmnIssue(node.Id, BpmnIssueCodes.MissingTaskType, $"Service task '{node.Id}' needs <flowd:taskDefinition type=\"...\"/>."));
                RejectConditions(node, outgoing, issues);
                break;
            case ParallelGateway or StartEvent or EndEvent or ManualTask or ServiceTask:
                RejectConditions(node, outgoing, issues);
                break;
        }
    }

    private static void ValidateMappings(ServiceTask task, List<BpmnIssue> issues)
    {
        foreach (var mapping in task.Inputs.Concat(task.Outputs))
        {
            if (string.IsNullOrWhiteSpace(mapping.Target) || string.IsNullOrWhiteSpace(mapping.Source))
            {
                issues.Add(new BpmnIssue(task.Id, BpmnIssueCodes.InvalidMapping, $"A mapping on '{task.Id}' needs both source and target."));
            }
            else if (mapping.Target.StartsWith("_flowd", StringComparison.Ordinal))
            {
                issues.Add(new BpmnIssue(task.Id, BpmnIssueCodes.InvalidMapping, $"Mapping target '{mapping.Target}' uses the reserved prefix '_flowd' (FR-VAR-003)."));
            }
        }
    }

    private static void ValidateExclusiveGateway(ExclusiveGateway gateway, List<SequenceFlow> outgoing, List<BpmnIssue> issues)
    {
        if (gateway.DefaultFlowId is { } defaultId && !outgoing.Any(f => string.Equals(f.Id, defaultId, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(gateway.Id, BpmnIssueCodes.InvalidDefaultFlow, $"Default flow '{defaultId}' is not an outgoing flow of gateway '{gateway.Id}'."));
        }

        if (outgoing.Count <= 1)
        {
            return;
        }

        foreach (var flow in outgoing.Where(f => string.IsNullOrWhiteSpace(f.Condition) && !string.Equals(f.Id, gateway.DefaultFlowId, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.MissingCondition, $"Outgoing flow '{flow.Id}' of gateway '{gateway.Id}' needs a condition or must be the default flow."));
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
