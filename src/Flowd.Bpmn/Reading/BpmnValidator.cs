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
    private static void ValidateScope(string? scopeId, IReadOnlyList<FlowNode> nodes, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues, bool inEventSubProcess = false, bool adHoc = false)
    {
        if (!adHoc && !nodes.Any(n => n is StartEvent))
        {
            issues.Add(new BpmnIssue(scopeId, BpmnIssueCodes.NoStartEvent, $"'{scopeId}' has no (supported) start event."));
        }

        ValidateLinks(nodes, issues);
        var knownIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ValidateListeners(node, issues);
            ValidateNode(node, nodes, flows, issues, inEventSubProcess);
            if (node is SubProcess sub)
            {
                ValidateScope(sub.Id, sub.Nodes, sub.Flows, issues, sub.IsEventSubProcess, sub.IsAdHoc);
            }
        }

        foreach (var flow in flows)
        {
            ValidateFlow(flow, knownIds, issues);
        }
    }

    private static void ValidateNode(FlowNode node, IReadOnlyList<FlowNode> nodes, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues, bool inEventSubProcess = false)
    {
        var outgoing = flows.Where(f => string.Equals(f.SourceRef, node.Id, StringComparison.Ordinal)).ToList();
        switch (node)
        {
            case UnsupportedNode unsupported:
                issues.Add(new BpmnIssue(node.Id, BpmnIssueCodes.UnsupportedElement, $"Element '{node.Id}' of type '{unsupported.ElementType}' is not supported."));
                break;
            case BoundaryEvent boundary:
                ValidateBoundary(boundary, nodes, flows, issues);
                break;
            case IntermediateCatchEvent catchEvent:
                ValidateDefinition(catchEvent.Id, catchEvent.Definition, allowCycle: false, requireCorrelation: true, issues);
                break;
            case ReceiveTask receive:
                ValidateDefinition(receive.Id, receive.Message, allowCycle: false, requireCorrelation: true, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case EventBasedGateway:
                ValidateEventGateway(node, nodes, outgoing, issues);
                break;
            case StartEvent { Definition: { } startDefinition } start:
                ValidateDefinition(start.Id, startDefinition, allowCycle: true, requireCorrelation: false, issues, allowError: inEventSubProcess, allowEscalation: inEventSubProcess);
                RejectConditions(node, outgoing, issues);
                break;
            case ExclusiveGateway gateway:
                ValidateDiverging(gateway.Id, gateway.DefaultFlowId, outgoing, issues);
                break;
            case InclusiveGateway gateway:
                ValidateDiverging(gateway.Id, gateway.DefaultFlowId, outgoing, issues);
                break;
            case ServiceTask task:
                ValidateServiceTask(task, issues);
                ValidateLoop(task.Id, task.Loop, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case SubProcess sub:
                ValidateMappings(sub.Id, sub.Inputs.Concat(sub.Outputs), issues);
                ValidateLoop(sub.Id, sub.Loop, issues);
                if (sub.IsEventSubProcess)
                {
                    ValidateEventSubProcess(sub, flows, issues);
                }
                else
                {
                    RejectConditions(node, outgoing, issues);
                }

                break;
            case CallActivity call:
                if (string.IsNullOrWhiteSpace(call.CalledElement))
                {
                    issues.Add(new BpmnIssue(call.Id, BpmnIssueCodes.InvalidCall, $"Call activity '{call.Id}' needs a calledElement (the bpmnProcessId to call)."));
                }

                ValidateMappings(call.Id, call.Inputs.Concat(call.Outputs), issues);
                ValidateLoop(call.Id, call.Loop, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case ScriptTask script:
                ValidateScript(script, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case UserTask userTask:
                ValidateMappings(userTask.Id, userTask.Inputs.Concat(userTask.Outputs), issues);
                ValidateLoop(userTask.Id, userTask.Loop, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case IntermediateThrowEvent throwEvent:
                ValidateThrowEvent(throwEvent, issues);
                RejectConditions(node, outgoing, issues);
                break;
            case EndEvent { Error: not null } or EndEvent { Escalation: not null } or EndEvent { MessageJobType: not null } or EndEvent { Message: not null }:
                RejectConditions(node, outgoing, issues);
                break;
            case ParallelGateway or StartEvent or EndEvent or ManualTask:
                RejectConditions(node, outgoing, issues);
                break;
        }
    }

    private static void ValidateListeners(FlowNode node, List<BpmnIssue> issues)
    {
        foreach (var listener in node.Listeners)
        {
            var allowed = listener.Event is "start" or "end" || (node is UserTask && listener.Event is "created" or "assigned" or "completed");
            if (!allowed || string.IsNullOrWhiteSpace(listener.JobType))
            {
                issues.Add(new BpmnIssue(node.Id, BpmnIssueCodes.InvalidEventDefinition, $"Listener event '{listener.Event}' of '{node.Id}' is not supported (use start and end; created, assigned and completed on user tasks) or has no type."));
            }
        }
    }

    private static void ValidateLinks(IReadOnlyList<FlowNode> nodes, List<BpmnIssue> issues)
    {
        var catches = nodes.OfType<IntermediateCatchEvent>().Select(c => (c.Id, Link: c.Definition as LinkEventDefinition)).Where(c => c.Link is not null).ToList();
        foreach (var duplicate in catches.GroupBy(c => c.Link!.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(new BpmnIssue(duplicate.First().Id, BpmnIssueCodes.InvalidEventDefinition, $"Link name '{duplicate.Key}' is used by more than one catch event in the same scope."));
        }

        foreach (var thrower in nodes.OfType<IntermediateThrowEvent>().Where(t => t.Definition is LinkEventDefinition))
        {
            var name = ((LinkEventDefinition)thrower.Definition!).Name;
            if (!catches.Any(c => string.Equals(c.Link!.Name, name, StringComparison.Ordinal)))
            {
                issues.Add(new BpmnIssue(thrower.Id, BpmnIssueCodes.InvalidEventDefinition, $"Link throw event '{thrower.Id}' has no catch event named '{name}' in the same scope."));
            }
        }
    }

    private static void ValidateBoundary(BoundaryEvent boundary, IReadOnlyList<FlowNode> nodes, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues)
    {
        var attached = nodes.FirstOrDefault(n => string.Equals(n.Id, boundary.AttachedToRef, StringComparison.Ordinal));
        if (attached is not (ServiceTask or SubProcess or ReceiveTask or UserTask or CallActivity))
        {
            issues.Add(new BpmnIssue(boundary.Id, BpmnIssueCodes.InvalidBoundary, $"Boundary event '{boundary.Id}' must be attached to a task or sub-process in the same scope (attachedToRef='{boundary.AttachedToRef}')."));
        }

        if (flows.Any(f => string.Equals(f.TargetRef, boundary.Id, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(boundary.Id, BpmnIssueCodes.InvalidBoundary, $"Boundary event '{boundary.Id}' must not have incoming flows."));
        }

        ValidateDefinition(boundary.Id, boundary.Definition, allowCycle: !boundary.CancelActivity, requireCorrelation: true, issues, allowError: boundary.CancelActivity, allowEscalation: true);
    }

    private static void ValidateEventGateway(FlowNode gateway, IReadOnlyList<FlowNode> nodes, List<SequenceFlow> outgoing, List<BpmnIssue> issues)
    {
        if (outgoing.Count < 2)
        {
            issues.Add(new BpmnIssue(gateway.Id, BpmnIssueCodes.InvalidGatewayTarget, $"Event-based gateway '{gateway.Id}' needs at least two outgoing flows."));
        }

        foreach (var flow in outgoing)
        {
            var target = nodes.FirstOrDefault(n => string.Equals(n.Id, flow.TargetRef, StringComparison.Ordinal));
            if (target is not (IntermediateCatchEvent or ReceiveTask))
            {
                issues.Add(new BpmnIssue(flow.Id, BpmnIssueCodes.InvalidGatewayTarget, $"Flow '{flow.Id}' of event-based gateway '{gateway.Id}' must lead to a catch event or receive task."));
            }
        }
    }

    private static void ValidateDefinition(string ownerId, EventDefinition definition, bool allowCycle, bool requireCorrelation, List<BpmnIssue> issues, bool allowError = false, bool allowEscalation = false)
    {
        switch (definition)
        {
            case TimerEventDefinition timer:
                if (timer.Kind == TimerKind.Cycle && !allowCycle)
                {
                    issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidTimer, $"A timer cycle is only allowed on start events and non-interrupting boundary events ('{ownerId}')."));
                }
                else if (!timer.Expression.TrimStart().StartsWith('=') && !IsoTimer.IsValid(timer.Kind, timer.Expression))
                {
                    issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidTimer, $"Timer '{timer.Expression}' of '{ownerId}' is not a valid ISO 8601 {timer.Kind.ToString().ToLowerInvariant()}."));
                }

                break;
            case ErrorEventDefinition when !allowError:
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"'{ownerId}' cannot use an error definition (only interrupting boundary events and error end events can)."));
                break;
            case LinkEventDefinition { Name: var linkName } when string.IsNullOrWhiteSpace(linkName):
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"The link of '{ownerId}' has no name."));
                break;
            case EscalationEventDefinition when !allowEscalation:
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"'{ownerId}' cannot use an escalation definition (only boundary events, event sub-process start events, escalation throw and end events can)."));
                break;
            case ConditionalEventDefinition { Condition: var condition } when !condition.TrimStart().StartsWith('=') || condition.Trim().Length < 2:
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"The condition of '{ownerId}' must be an expression starting with '='."));
                break;
            case SignalEventDefinition { Name: var signalName } when string.IsNullOrWhiteSpace(signalName):
                issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"The signal of '{ownerId}' has no name."));
                break;
            case MessageEventDefinition message:
                if (string.IsNullOrWhiteSpace(message.Name))
                {
                    issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"The message of '{ownerId}' has no name."));
                }
                else if (requireCorrelation && string.IsNullOrWhiteSpace(message.CorrelationKey))
                {
                    issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidEventDefinition, $"The message '{message.Name}' of '{ownerId}' needs <flowd:subscription correlationKey=\"= ...\"/>."));
                }

                break;
        }
    }

    private static void ValidateLoop(string ownerId, LoopCharacteristics? loop, List<BpmnIssue> issues)
    {
        if (loop is not null && (string.IsNullOrWhiteSpace(loop.InputCollection) || string.IsNullOrWhiteSpace(loop.InputElement)))
        {
            issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidLoop, $"Multi-instance '{ownerId}' needs <flowd:loopCharacteristics inputCollection=\"= ...\" inputElement=\"...\"/>."));
        }
        else if (loop is { OutputCollection: { } output } && (output.StartsWith("_flowd", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(loop.OutputElement)))
        {
            issues.Add(new BpmnIssue(ownerId, BpmnIssueCodes.InvalidLoop, $"Multi-instance '{ownerId}' needs an outputElement expression and a non-reserved outputCollection name."));
        }
    }

    private static void ValidateEventSubProcess(SubProcess sub, IReadOnlyList<SequenceFlow> flows, List<BpmnIssue> issues)
    {
        if (flows.Any(f => string.Equals(f.SourceRef, sub.Id, StringComparison.Ordinal) || string.Equals(f.TargetRef, sub.Id, StringComparison.Ordinal)))
        {
            issues.Add(new BpmnIssue(sub.Id, BpmnIssueCodes.InvalidEventSubProcess, $"Event sub-process '{sub.Id}' must not have incoming or outgoing flows."));
        }

        var start = sub.Nodes.OfType<StartEvent>().FirstOrDefault();
        if (start?.Definition is null)
        {
            issues.Add(new BpmnIssue(sub.Id, BpmnIssueCodes.InvalidEventSubProcess, $"Event sub-process '{sub.Id}' needs a timer, message, signal, error or escalation start event."));
        }
        else if (start.Definition is ErrorEventDefinition && !start.IsInterrupting)
        {
            issues.Add(new BpmnIssue(sub.Id, BpmnIssueCodes.InvalidEventSubProcess, $"An error start event of '{sub.Id}' must be interrupting."));
        }
        else if (start.Definition is TimerEventDefinition { Kind: TimerKind.Cycle } && start.IsInterrupting)
        {
            issues.Add(new BpmnIssue(sub.Id, BpmnIssueCodes.InvalidTimer, $"A timer cycle on '{sub.Id}' is only allowed for non-interrupting event sub-processes."));
        }
    }

    private static void ValidateThrowEvent(IntermediateThrowEvent throwEvent, List<BpmnIssue> issues)
    {
        if (throwEvent.Definition is SignalEventDefinition { Name: { Length: > 0 } })
        {
            return;
        }

        if (throwEvent.Definition is null && throwEvent.JobType is null)
        {
            return; // a none throw event passes the token on
        }

        if (string.IsNullOrWhiteSpace(throwEvent.JobType) && throwEvent.Definition is null)
        {
            issues.Add(new BpmnIssue(throwEvent.Id, BpmnIssueCodes.MissingTaskType, $"Message throw event '{throwEvent.Id}' needs <flowd:taskDefinition type=\"...\"/>."));
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
