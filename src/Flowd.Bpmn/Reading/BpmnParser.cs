using System.Xml;
using System.Xml.Linq;
using Flowd.Bpmn.Model;

namespace Flowd.Bpmn.Reading;

/// <summary>
/// XXE-safe BPMN XML parser (FR-SEC-006). Parsing is lenient: unsupported elements and dangling references are kept in the
/// model so <see cref="BpmnValidator"/> can report all of them at once.
/// </summary>
public sealed class BpmnParser
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Flowd = "http://flowd.io/schema/1.0";

    /// <summary>Elements inside a process that carry no execution semantics.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "documentation", "extensionElements", "laneSet", "textAnnotation", "association", "group", "property",
        "ioSpecification", "dataInputAssociation", "dataOutputAssociation", "multiInstanceLoopCharacteristics", "standardLoopCharacteristics",
    };

    private readonly Dictionary<string, MessageEventDefinition> _messages;
    private readonly Dictionary<string, string> _signals;
    private readonly Dictionary<string, string> _errors;
    private readonly Dictionary<string, string> _escalations;

    private BpmnParser(Dictionary<string, MessageEventDefinition> messages, Dictionary<string, string> signals, Dictionary<string, string> errors, Dictionary<string, string> escalations)
    {
        _messages = messages;
        _signals = signals;
        _errors = errors;
        _escalations = escalations;
    }

    /// <summary>Parses the stream. Returns null and fills <paramref name="issue"/> when the XML itself cannot be read.</summary>
    public static BpmnDefinitions? TryParse(Stream xml, out BpmnIssue? issue)
    {
        ArgumentNullException.ThrowIfNull(xml);
        issue = null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(xml, settings);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var parser = new BpmnParser(ReadMessages(document), ReadNames(document, "signal", "name"), ReadNames(document, "error", "errorCode"), ReadNames(document, "escalation", "escalationCode"));
            return new BpmnDefinitions([.. document.Descendants(Bpmn + "process").Select(parser.ParseProcess)]);
        }
        catch (XmlException ex)
        {
            var isDtd = ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase);
            issue = new BpmnIssue(
                null,
                isDtd ? BpmnIssueCodes.DtdNotAllowed : BpmnIssueCodes.MalformedXml,
                isDtd ? "DTD / external entities are not allowed." : $"Malformed XML: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, string> ReadNames(XDocument document, string element, string attribute)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in document.Descendants(Bpmn + element))
        {
            if ((string?)e.Attribute("id") is { } id && (string?)e.Attribute(attribute) is { } value)
            {
                result[id] = value;
            }
        }

        return result;
    }

    private static Dictionary<string, MessageEventDefinition> ReadMessages(XDocument document)
    {
        var result = new Dictionary<string, MessageEventDefinition>(StringComparer.Ordinal);
        foreach (var message in document.Descendants(Bpmn + "message"))
        {
            var id = (string?)message.Attribute("id");
            var name = (string?)message.Attribute("name");
            if (id is null || name is null)
            {
                continue;
            }

            var key = (string?)message.Element(Bpmn + "extensionElements")?.Element(Flowd + "subscription")?.Attribute("correlationKey");
            result[id] = new MessageEventDefinition { Name = name, CorrelationKey = key };
        }

        return result;
    }

    private BpmnProcess ParseProcess(XElement process) => new(
        (string?)process.Attribute("id"),
        (string?)process.Attribute("name"),
        string.Equals((string?)process.Attribute("isExecutable"), "true", StringComparison.OrdinalIgnoreCase),
        ParseNodes(process),
        ParseFlows(process),
        (string?)process.Element(Bpmn + "extensionElements")?.Element(Flowd + "versionTag")?.Attribute("value"));

    private FlowNode[] ParseNodes(XElement scope) =>
        [.. scope.Elements().Where(IsNode).Select(ParseNode)];

    private static SequenceFlow[] ParseFlows(XElement scope) =>
        [.. scope.Elements(Bpmn + "sequenceFlow").Select(ParseFlow)];

    private static bool IsNode(XElement element) =>
        element.Name.Namespace == Bpmn
        && !string.Equals(element.Name.LocalName, "sequenceFlow", StringComparison.Ordinal)
        && !Ignored.Contains(element.Name.LocalName);

    private static SequenceFlow ParseFlow(XElement flow) => new(
        (string?)flow.Attribute("id") ?? string.Empty,
        (string?)flow.Attribute("sourceRef") ?? string.Empty,
        (string?)flow.Attribute("targetRef") ?? string.Empty,
        flow.Element(Bpmn + "conditionExpression")?.Value.Trim());

    private FlowNode ParseNode(XElement e)
    {
        var node = ParseNodeCore(e);
        node.Listeners = ReadListeners(e);
        return node;
    }

    private static ListenerDefinition[] ReadListeners(XElement e) =>
        [.. (e.Element(Bpmn + "extensionElements")?.Elements() ?? [])
            .Where(x => x.Name == Flowd + "executionListeners" || x.Name == Flowd + "taskListeners")
            .SelectMany(x => x.Elements(Flowd + "listener"))
            .Where(l => l.Attribute("event") is not null && l.Attribute("type") is not null)
            .Select(l => new ListenerDefinition(((string)l.Attribute("event")!).ToLowerInvariant(), (string)l.Attribute("type")!))];

    private FlowNode ParseNodeCore(XElement e)
    {
        var id = (string?)e.Attribute("id") ?? string.Empty;
        var name = (string?)e.Attribute("name");
        return e.Name.LocalName switch
        {
            "startEvent" => ParseStartEvent(e, id, name),
            "endEvent" when !HasEventDefinition(e) => new EndEvent { Id = id, Name = name },
            "endEvent" when HasOnlyEventDefinition(e, "terminateEventDefinition") => new EndEvent { Id = id, Name = name, IsTerminate = true },
            "endEvent" when HasOnlyEventDefinition(e, "signalEventDefinition") && ReadDefinition(e) is SignalEventDefinition endSignal => new EndEvent { Id = id, Name = name, Signal = endSignal },
            "endEvent" when HasOnlyEventDefinition(e, "compensateEventDefinition") => new EndEvent { Id = id, Name = name, Compensation = ReadCompensation(e) },
            "endEvent" when HasOnlyEventDefinition(e, "errorEventDefinition") => new EndEvent { Id = id, Name = name, Error = ReadError(e) },
            "endEvent" when HasOnlyEventDefinition(e, "escalationEventDefinition") => new EndEvent { Id = id, Name = name, Escalation = ReadEscalation(e) },
            "endEvent" when HasOnlyEventDefinition(e, "messageEventDefinition") && TaskType(e) is { } endJob => new EndEvent { Id = id, Name = name, MessageJobType = endJob },
            "endEvent" when HasOnlyEventDefinition(e, "messageEventDefinition") && ReadDefinition(e) is MessageEventDefinition endMessage => new EndEvent { Id = id, Name = name, Message = endMessage },
            "intermediateThrowEvent" when !HasEventDefinition(e) => new IntermediateThrowEvent { Id = id, Name = name },
            "intermediateThrowEvent" when HasOnlyEventDefinition(e, "messageEventDefinition") && TaskType(e) is { } throwJob => new IntermediateThrowEvent { Id = id, Name = name, JobType = throwJob },
            "intermediateThrowEvent" when HasOnlyEventDefinition(e, "compensateEventDefinition") => new IntermediateThrowEvent { Id = id, Name = name, Definition = ReadCompensation(e) },
            "intermediateThrowEvent" when HasOnlyEventDefinition(e, "linkEventDefinition") => new IntermediateThrowEvent { Id = id, Name = name, Definition = ReadLink(e) },
            "intermediateThrowEvent" when HasOnlyEventDefinition(e, "escalationEventDefinition") => new IntermediateThrowEvent { Id = id, Name = name, Definition = ReadEscalation(e) },
            "intermediateThrowEvent" when ReadDefinition(e) is MessageEventDefinition publishing => new IntermediateThrowEvent { Id = id, Name = name, Definition = publishing },
            "intermediateThrowEvent" when ReadDefinition(e) is SignalEventDefinition signal => new IntermediateThrowEvent { Id = id, Name = name, Definition = signal },
            "userTask" => ParseUserTask(e, id, name),
            "intermediateCatchEvent" when ReadDefinition(e) is { } definition => new IntermediateCatchEvent { Id = id, Name = name, Definition = definition },
            "boundaryEvent" when ReadDefinition(e) is { } definition && (string?)e.Attribute("attachedToRef") is { } attached =>
                new BoundaryEvent { Id = id, Name = name, AttachedToRef = attached, CancelActivity = !string.Equals((string?)e.Attribute("cancelActivity"), "false", StringComparison.OrdinalIgnoreCase), Definition = definition, CompensationHandlerId = HandlerOf(e) },
            "receiveTask" when ReadDefinition(e, fromMessageRef: true) is MessageEventDefinition message => new ReceiveTask { Id = id, Name = name, Message = message },
            "eventBasedGateway" => new EventBasedGateway { Id = id, Name = name },
            "serviceTask" or "sendTask" or "businessRuleTask" => ParseJobTask(e, id, name),
            "scriptTask" => ParseScriptTask(e, id, name),
            "manualTask" => new ManualTask { Id = id, Name = name },
            "subProcess" => ParseSubProcess(e, id, name),
            "adHocSubProcess" => ParseSubProcess(e, id, name),
            "callActivity" => ParseCallActivity(e, id, name),
            "exclusiveGateway" => new ExclusiveGateway { Id = id, Name = name, DefaultFlowId = (string?)e.Attribute("default") },
            "inclusiveGateway" => new InclusiveGateway { Id = id, Name = name, DefaultFlowId = (string?)e.Attribute("default") },
            "parallelGateway" => new ParallelGateway { Id = id, Name = name },
            _ => new UnsupportedNode { Id = id, Name = name, ElementType = DescribeUnsupported(e) }, // non-union
        };
    }

    private FlowNode ParseStartEvent(XElement e, string id, string? name)
    {
        if (!HasEventDefinition(e))
        {
            return new StartEvent { Id = id, Name = name };
        }

        return ReadDefinition(e) is { } definition
            ? new StartEvent { Id = id, Name = name, Definition = definition, IsInterrupting = !string.Equals((string?)e.Attribute("isInterrupting"), "false", StringComparison.OrdinalIgnoreCase) }
            : new UnsupportedNode { Id = id, Name = name, ElementType = DescribeUnsupported(e) };
    }

    /// <summary>Reads a timer or message event definition; null for definitions this version does not support.</summary>
    private EventDefinition? ReadDefinition(XElement e, bool fromMessageRef = false)
    {
        if (e.Element(Bpmn + "timerEventDefinition") is { } timer)
        {
            return ReadTimer(timer);
        }

        if (e.Element(Bpmn + "messageEventDefinition") is { } message)
        {
            return _messages.GetValueOrDefault((string?)message.Attribute("messageRef") ?? string.Empty);
        }

        if (e.Element(Bpmn + "signalEventDefinition") is { } signal)
        {
            return _signals.TryGetValue((string?)signal.Attribute("signalRef") ?? string.Empty, out var name) ? new SignalEventDefinition { Name = name } : null;
        }

        if (e.Element(Bpmn + "compensateEventDefinition") is not null)
        {
            return ReadCompensation(e);
        }

        if (e.Element(Bpmn + "conditionalEventDefinition") is { } conditional)
        {
            return new ConditionalEventDefinition { Condition = (string?)conditional.Element(Bpmn + "condition") ?? string.Empty };
        }

        if (e.Element(Bpmn + "linkEventDefinition") is not null)
        {
            return ReadLink(e);
        }

        if (e.Element(Bpmn + "escalationEventDefinition") is not null)
        {
            return ReadEscalation(e);
        }

        if (e.Element(Bpmn + "errorEventDefinition") is not null)
        {
            return ReadError(e);
        }

        return fromMessageRef ? _messages.GetValueOrDefault((string?)e.Attribute("messageRef") ?? string.Empty) : null;
    }

    /// <summary>An error definition; without an <c>errorRef</c> it catches every error.</summary>
    private ErrorEventDefinition ReadError(XElement e)
    {
        var reference = (string?)e.Element(Bpmn + "errorEventDefinition")?.Attribute("errorRef");
        return new ErrorEventDefinition { Code = reference is not null && _errors.TryGetValue(reference, out var code) ? code : null };
    }

    private static LinkEventDefinition ReadLink(XElement e) =>
        new() { Name = (string?)e.Element(Bpmn + "linkEventDefinition")?.Attribute("name") ?? string.Empty };

    /// <summary>An escalation definition; without an <c>escalationRef</c> it catches every escalation.</summary>
    private EscalationEventDefinition ReadEscalation(XElement e)
    {
        var reference = (string?)e.Element(Bpmn + "escalationEventDefinition")?.Attribute("escalationRef");
        return new EscalationEventDefinition { Code = reference is not null && _escalations.TryGetValue(reference, out var code) ? code : null };
    }

    private static string? TaskType(XElement e) =>
        (string?)e.Element(Bpmn + "extensionElements")?.Element(Flowd + "taskDefinition")?.Attribute("type");

    private static UserTask ParseUserTask(XElement e, string id, string? name)
    {
        var extensions = e.Element(Bpmn + "extensionElements");
        var assignment = extensions?.Element(Flowd + "assignmentDefinition");
        var mapping = extensions?.Element(Flowd + "ioMapping");
        return new UserTask
        {
            Id = id,
            Name = name,
            Assignee = (string?)assignment?.Attribute("assignee"),
            Loop = ReadLoop(e),
            CandidateGroups = Split((string?)assignment?.Attribute("candidateGroups")),
            CandidateUsers = Split((string?)assignment?.Attribute("candidateUsers")),
            DueDate = (string?)extensions?.Element(Flowd + "taskSchedule")?.Attribute("dueDate"),
            FollowUpDate = (string?)extensions?.Element(Flowd + "taskSchedule")?.Attribute("followUpDate"),
            Priority = int.TryParse((string?)extensions?.Element(Flowd + "taskSchedule")?.Attribute("priority"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var priority) ? Math.Min(priority, 100) : 50,
            FormKey = (string?)extensions?.Element(Flowd + "formDefinition")?.Attribute("formKey"),
            Inputs = ParseMappings(mapping, "input"),
            Outputs = ParseMappings(mapping, "output"),
            PropagateAllOutputs = !string.Equals((string?)mapping?.Attribute("propagateAll"), "false", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string[] Split(string? list) =>
        list is null ? [] : [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static TimerEventDefinition? ReadTimer(XElement timer)
    {
        foreach (var (element, kind) in new[] { ("timeDate", TimerKind.Date), ("timeDuration", TimerKind.Duration), ("timeCycle", TimerKind.Cycle) })
        {
            if (timer.Element(Bpmn + element)?.Value.Trim() is { Length: > 0 } text)
            {
                return new TimerEventDefinition { Kind = kind, Expression = text };
            }
        }

        return null;
    }

    private static bool IsTrue(XElement e, string attribute) =>
        string.Equals((string?)e.Attribute(attribute), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <c>multiInstanceLoopCharacteristics</c> with its <c>flowd:loopCharacteristics</c> settings (FR-EXEC-012).</summary>
    private static LoopCharacteristics? ReadLoop(XElement e)
    {
        var loop = e.Element(Bpmn + "multiInstanceLoopCharacteristics");
        if (loop is null)
        {
            return null;
        }

        var settings = loop.Element(Bpmn + "extensionElements")?.Element(Flowd + "loopCharacteristics");
        return new LoopCharacteristics(
            string.Equals((string?)loop.Attribute("isSequential"), "true", StringComparison.OrdinalIgnoreCase),
            (string?)settings?.Attribute("inputCollection") ?? string.Empty,
            (string?)settings?.Attribute("inputElement") ?? string.Empty,
            (string?)settings?.Attribute("outputCollection"),
            (string?)settings?.Attribute("outputElement"),
            loop.Element(Bpmn + "completionCondition")?.Value.Trim() ?? (string?)settings?.Attribute("completionCondition"));
    }

    private static CallActivity ParseCallActivity(XElement e, string id, string? name)
    {
        var extensions = e.Element(Bpmn + "extensionElements");
        var call = extensions?.Element(Flowd + "calledElement");
        var mapping = extensions?.Element(Flowd + "ioMapping");
        return new CallActivity
        {
            Id = id,
            Name = name,
            CalledElement = (string?)call?.Attribute("processId") ?? (string?)e.Attribute("calledElement"),
            BindingType = string.Equals((string?)call?.Attribute("bindingType"), "versionTag", StringComparison.OrdinalIgnoreCase) ? "versionTag" : "latest",
            VersionTag = (string?)call?.Attribute("versionTag"),
            PropagateAllChildVariables = !string.Equals((string?)call?.Attribute("propagateAllChildVariables"), "false", StringComparison.OrdinalIgnoreCase),
            PropagateAllParentVariables = !string.Equals((string?)call?.Attribute("propagateAllParentVariables"), "false", StringComparison.OrdinalIgnoreCase),
            Inputs = ParseMappings(mapping, "input"),
            Outputs = ParseMappings(mapping, "output"),
            Loop = ReadLoop(e),
        };
    }

    private SubProcess ParseSubProcess(XElement e, string id, string? name)
    {
        var mapping = e.Element(Bpmn + "extensionElements")?.Element(Flowd + "ioMapping");
        return new SubProcess
        {
            Id = id,
            Name = name,
            Nodes = ParseNodes(e),
            Flows = ParseFlows(e),
            IsEventSubProcess = IsTrue(e, "triggeredByEvent"),
            IsAdHoc = e.Name.LocalName == "adHocSubProcess",
            Loop = ReadLoop(e),
            Inputs = ParseMappings(mapping, "input"),
            Outputs = ParseMappings(mapping, "output"),
        };
    }

    private static ScriptTask ParseScriptTask(XElement e, string id, string? name)
    {
        var script = e.Element(Bpmn + "extensionElements")?.Element(Flowd + "script");
        return new ScriptTask
        {
            Id = id,
            Name = name,
            Expression = (string?)script?.Attribute("expression"),
            ResultVariable = (string?)script?.Attribute("resultVariable"),
        };
    }

    private static CompensationEventDefinition ReadCompensation(XElement e) =>
        new() { ActivityRef = (string?)e.Element(Bpmn + "compensateEventDefinition")?.Attribute("activityRef") };

    /// <summary>The compensation handler of a boundary event: the target of the association that starts at it.</summary>
    private static string? HandlerOf(XElement boundary) =>
        boundary.Element(Bpmn + "compensateEventDefinition") is null
            ? null
            : (string?)boundary.Parent?.Elements(Bpmn + "association").FirstOrDefault(a => (string?)a.Attribute("sourceRef") == (string?)boundary.Attribute("id"))?.Attribute("targetRef");

    private static string? InternalTaskType(XElement? decision, XElement? connector)
    {
        if (decision is not null)
        {
            return ServiceTask.DecisionTaskType;
        }

        return (string?)connector?.Attribute("type") switch
        {
            { } t when string.Equals(t, "http", StringComparison.OrdinalIgnoreCase) => ServiceTask.HttpConnectorTaskType,
            { } t when string.Equals(t, "email", StringComparison.OrdinalIgnoreCase) => ServiceTask.EmailConnectorTaskType,
            { } t when string.Equals(t, "rabbitmq", StringComparison.OrdinalIgnoreCase) => ServiceTask.QueueConnectorTaskType,
            { } t when string.Equals(t, "graphql", StringComparison.OrdinalIgnoreCase) => ServiceTask.GraphQlConnectorTaskType,
            { } t when string.Equals(t, "slack", StringComparison.OrdinalIgnoreCase) => ServiceTask.SlackConnectorTaskType,
            { } t when string.Equals(t, "sql", StringComparison.OrdinalIgnoreCase) => ServiceTask.SqlConnectorTaskType,
            { } t when string.Equals(t, "kafka", StringComparison.OrdinalIgnoreCase) => ServiceTask.KafkaConnectorTaskType,
            _ => null, // non-union
        };
    }

    /// <summary>The settings of a <c>flowd:connector</c> element become task headers the built-in connector worker reads (url, method, body, headers, resultVariable, failOnError).</summary>
    private static IEnumerable<KeyValuePair<string, string>> ConnectorHeaders(XElement? connector) =>
        connector is null
            ? []
            : connector.Attributes().Where(a => a.Name.LocalName != "type").Select(a => new KeyValuePair<string, string>(a.Name.LocalName, a.Value));

    /// <summary>A business rule task with <c>flowd:calledDecision</c> becomes an internal decision job carrying the decision id and result variable.</summary>
    private static IEnumerable<KeyValuePair<string, string>> DecisionHeaders(XElement? decision) =>
        decision is null
            ? []
            : [new("decisionId", (string?)decision.Attribute("decisionId") ?? string.Empty), new("resultVariable", (string?)decision.Attribute("resultVariable") ?? "decisionResult")];

    private static ServiceTask ParseJobTask(XElement e, string id, string? name)
    {
        var extensions = e.Element(Bpmn + "extensionElements");
        var definition = extensions?.Element(Flowd + "taskDefinition");
        var mapping = extensions?.Element(Flowd + "ioMapping");
        var decision = extensions?.Element(Flowd + "calledDecision");
        var connector = extensions?.Element(Flowd + "connector");
        return new ServiceTask
        {
            Id = id,
            Name = name,
            BpmnType = e.Name.LocalName,
            Loop = ReadLoop(e),
            TaskType = InternalTaskType(decision, connector) ?? (string?)definition?.Attribute("type"),
            Retries = (string?)definition?.Attribute("retries"),
            Inputs = ParseMappings(mapping, "input"),
            Outputs = ParseMappings(mapping, "output"),
            PropagateAllOutputs = !string.Equals((string?)mapping?.Attribute("propagateAll"), "false", StringComparison.OrdinalIgnoreCase),
            Headers = (extensions?.Element(Flowd + "taskHeaders")?.Elements(Flowd + "header") ?? [])
                .Where(h => h.Attribute("key") is not null)
                .GroupBy(h => (string)h.Attribute("key")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (string?)g.Last().Attribute("value") ?? string.Empty, StringComparer.Ordinal)
                .Concat(DecisionHeaders(decision))
                .Concat(ConnectorHeaders(connector))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    private static IoMapping[] ParseMappings(XElement? mapping, string kind) =>
        [.. (mapping?.Elements(Flowd + kind) ?? []).Select(m => new IoMapping((string?)m.Attribute("source") ?? string.Empty, (string?)m.Attribute("target") ?? string.Empty))];

    private static bool HasEventDefinition(XElement e) =>
        e.Elements().Any(c => c.Name.LocalName.EndsWith("EventDefinition", StringComparison.Ordinal));

    private static bool HasOnlyEventDefinition(XElement e, string localName) =>
        e.Elements().Count(c => c.Name.LocalName.EndsWith("EventDefinition", StringComparison.Ordinal)) == 1
        && e.Element(Bpmn + localName) is not null;

    private static string DescribeUnsupported(XElement e)
    {
        var definition = e.Elements().FirstOrDefault(c => c.Name.LocalName.EndsWith("EventDefinition", StringComparison.Ordinal));
        return definition is null ? e.Name.LocalName : $"{e.Name.LocalName} ({definition.Name.LocalName})";
    }
}
