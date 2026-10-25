using System.Xml;
using System.Xml.Linq;

namespace Flowd.Expressions.Dmn;

/// <summary>A DMN file that could not be read or is not valid.</summary>
public sealed record DmnInvalid(string Code, string Message);

/// <summary>Result of reading a DMN file.</summary>
public union DmnReadResult(DmnDefinitions, DmnInvalid);

/// <summary>Reads DMN 1.x XML (decision tables and literal expressions) without resolving external entities.</summary>
public static class DmnParser
{
    public static DmnReadResult Read(Stream xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var decisions = document.Descendants().Where(e => e.Name.LocalName == "decision").Select(ParseDecision).ToList();
            return decisions.Any(d => d is null)
                ? new DmnInvalid("DMN_UNSUPPORTED", "A decision has neither a decision table nor a literal expression.")
                : new DmnDefinitions([.. decisions.OfType<Decision>()], [.. document.Descendants().Where(e => e.Name.LocalName == "businessKnowledgeModel").Select(ParseKnowledge)]);
        }
        catch (XmlException ex)
        {
            return new DmnInvalid("DMN_MALFORMED", ex.Message);
        }
    }

    private static Decision? ParseDecision(XElement decision)
    {
        var id = (string?)decision.Attribute("id") ?? string.Empty;
        var name = (string?)decision.Attribute("name");
        if (Child(decision, "decisionTable") is { } table)
        {
            return ParseTable(table, id, name, Requirements(decision));
        }

        return Child(decision, "literalExpression") is { } literal
            ? new LiteralExpressionDecision { Id = id, Name = name, Expression = Text(literal), Requires = Requirements(decision) }
            : null;
    }

    private static BusinessKnowledgeModel ParseKnowledge(XElement model)
    {
        var logic = Child(model, "encapsulatedLogic");
        return new BusinessKnowledgeModel(
            (string?)model.Attribute("id") ?? string.Empty,
            (string?)model.Attribute("name") ?? string.Empty,
            [.. Children(logic ?? model, "formalParameter").Select(p => (string?)p.Attribute("name") ?? string.Empty)],
            Text(logic is null ? null : Child(logic, "literalExpression")));
    }

    private static IReadOnlyList<string> Requirements(XElement decision) =>
        [.. Children(decision, "informationRequirement").SelectMany(r => Children(r, "requiredDecision")).Select(d => ((string?)d.Attribute("href") ?? string.Empty).TrimStart('#')).Where(id => id.Length > 0)];

    private static DecisionTable ParseTable(XElement table, string id, string? name, IReadOnlyList<string> requires) => new()
    {
        Id = id,
        Name = name,
        Requires = requires,
        HitPolicy = ParseHitPolicy((string?)table.Attribute("hitPolicy")),
        Aggregation = ParseAggregation((string?)table.Attribute("aggregation")),
        Inputs = [.. Children(table, "input").Select(i => new InputClause((string?)i.Attribute("label") ?? string.Empty, Text(Child(i, "inputExpression"))))],
        Outputs = [.. Children(table, "output").Select(o => new OutputClause((string?)o.Attribute("name") ?? (string?)o.Attribute("label") ?? "result", Values(o)))],
        Rules = [.. Children(table, "rule").Select(r => new Rule([.. Children(r, "inputEntry").Select(Text)], [.. Children(r, "outputEntry").Select(Text)]))],
    };

    private static IReadOnlyList<string> Values(XElement output) =>
        Child(output, "outputValues") is { } values ? [.. TestParts.Split(Text(values))] : [];

    private static HitPolicy ParseHitPolicy(string? text) => text?.ToUpperInvariant() switch
    {
        "FIRST" => HitPolicy.First,
        "ANY" => HitPolicy.Any,
        "PRIORITY" => HitPolicy.Priority,
        "COLLECT" => HitPolicy.Collect,
        "RULE ORDER" => HitPolicy.RuleOrder,
        "OUTPUT ORDER" => HitPolicy.OutputOrder,
        _ => HitPolicy.Unique, // non-union: attribute text, UNIQUE is the DMN default
    };

    private static Aggregation ParseAggregation(string? text) => text?.ToUpperInvariant() switch
    {
        "SUM" => Aggregation.Sum,
        "MIN" => Aggregation.Min,
        "MAX" => Aggregation.Max,
        "COUNT" => Aggregation.Count,
        _ => Aggregation.None, // non-union: attribute text
    };

    private static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement parent, string localName) => parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string Text(XElement? element) => (element is null ? null : Child(element, "text")?.Value)?.Trim() ?? string.Empty;
}
