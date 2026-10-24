namespace Flowd.Bpmn.Model;

/// <summary>Root of the closed BPMN element hierarchy; only this assembly may derive from it (UNI-004).</summary>
public closed class FlowNode
{
    public required string Id { get; init; }
}

/// <summary>Placeholder leaf proving the closed hierarchy compiles; real elements arrive in M1.</summary>
public sealed class StartEvent : FlowNode;

/// <summary>Placeholder leaf proving the closed hierarchy compiles; real elements arrive in M1.</summary>
public sealed class EndEvent : FlowNode;
