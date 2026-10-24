namespace Flowd.Expressions.Dmn;

/// <summary>DMN hit policies of a decision table (FR-DMN-001).</summary>
public enum HitPolicy
{
    Unique,
    First,
    Any,
    Priority,
    Collect,
    RuleOrder,
    OutputOrder,
}

/// <summary>Aggregation of a COLLECT table: none returns the list.</summary>
public enum Aggregation
{
    None,
    Sum,
    Min,
    Max,
    Count,
}

public sealed record InputClause(string Label, string Expression);

/// <summary>An output column; <see cref="Values"/> are the allowed outputs in priority order (used by PRIORITY and OUTPUT ORDER).</summary>
public sealed record OutputClause(string Name, IReadOnlyList<string> Values);

public sealed record Rule(IReadOnlyList<string> InputEntries, IReadOnlyList<string> OutputEntries);

/// <summary>A decision of a DMN definition: a decision table or a literal expression.</summary>
public closed class Decision
{
    public required string Id { get; init; }

    public string? Name { get; init; }
}

public sealed class DecisionTable : Decision
{
    public HitPolicy HitPolicy { get; init; } = HitPolicy.Unique;

    public Aggregation Aggregation { get; init; } = Aggregation.None;

    public IReadOnlyList<InputClause> Inputs { get; init; } = [];

    public IReadOnlyList<OutputClause> Outputs { get; init; } = [];

    public IReadOnlyList<Rule> Rules { get; init; } = [];
}

public sealed class LiteralExpressionDecision : Decision
{
    public required string Expression { get; init; }
}

/// <summary>All decisions of one DMN file.</summary>
public sealed record DmnDefinitions(IReadOnlyList<Decision> Decisions);
