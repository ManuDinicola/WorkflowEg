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

    private BpmnParser(Dictionary<string, MessageEventDefinition> messages) => _messages = messages;

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
            var parser = new BpmnParser(ReadMessages(document));
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
        ParseFlows(process));

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
        var id = (string?)e.Attribute("id") ?? string.Empty;
        var name = (string?)e.Attribute("name");
        return e.Name.LocalName switch
        {
            "startEvent" => ParseStartEvent(e, id, name),
            "endEvent" when !HasEventDefinition(e) => new EndEvent { Id = id, Name = name },
            "endEvent" when HasOnlyEventDefinition(e, "terminateEventDefinition") => new EndEvent { Id = id, Name = name, IsTerminate = true },
            "intermediateCatchEvent" when ReadDefinition(e) is { } definition => new IntermediateCatchEvent { Id = id, Name = name, Definition = definition },
            "boundaryEvent" when ReadDefinition(e) is { } definition && (string?)e.Attribute("attachedToRef") is { } attached =>
                new BoundaryEvent { Id = id, Name = name, AttachedToRef = attached, CancelActivity = !string.Equals((string?)e.Attribute("cancelActivity"), "false", StringComparison.OrdinalIgnoreCase), Definition = definition },
            "receiveTask" when ReadDefinition(e, fromMessageRef: true) is MessageEventDefinition message => new ReceiveTask { Id = id, Name = name, Message = message },
            "eventBasedGateway" => new EventBasedGateway { Id = id, Name = name },
            "serviceTask" or "sendTask" or "businessRuleTask" => ParseJobTask(e, id, name),
            "scriptTask" => ParseScriptTask(e, id, name),
            "manualTask" => new ManualTask { Id = id, Name = name },
            "subProcess" when !IsTrue(e, "triggeredByEvent") => ParseSubProcess(e, id, name),
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
            ? new StartEvent { Id = id, Name = name, Definition = definition }
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

        return fromMessageRef ? _messages.GetValueOrDefault((string?)e.Attribute("messageRef") ?? string.Empty) : null;
    }

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

    private SubProcess ParseSubProcess(XElement e, string id, string? name)
    {
        var mapping = e.Element(Bpmn + "extensionElements")?.Element(Flowd + "ioMapping");
        return new SubProcess
        {
            Id = id,
            Name = name,
            Nodes = ParseNodes(e),
            Flows = ParseFlows(e),
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

    private static ServiceTask ParseJobTask(XElement e, string id, string? name)
    {
        var extensions = e.Element(Bpmn + "extensionElements");
        var definition = extensions?.Element(Flowd + "taskDefinition");
        var mapping = extensions?.Element(Flowd + "ioMapping");
        return new ServiceTask
        {
            Id = id,
            Name = name,
            BpmnType = e.Name.LocalName,
            TaskType = (string?)definition?.Attribute("type"),
            Retries = (string?)definition?.Attribute("retries"),
            Inputs = ParseMappings(mapping, "input"),
            Outputs = ParseMappings(mapping, "output"),
            PropagateAllOutputs = !string.Equals((string?)mapping?.Attribute("propagateAll"), "false", StringComparison.OrdinalIgnoreCase),
            Headers = (extensions?.Element(Flowd + "taskHeaders")?.Elements(Flowd + "header") ?? [])
                .Where(h => h.Attribute("key") is not null)
                .GroupBy(h => (string)h.Attribute("key")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (string?)g.Last().Attribute("value") ?? string.Empty, StringComparer.Ordinal),
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
