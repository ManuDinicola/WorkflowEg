namespace Flowd.Bpmn.Model;

/// <summary>A sequence flow. <see cref="Condition"/> is the raw expression text, if any.</summary>
public sealed record SequenceFlow(string Id, string SourceRef, string TargetRef, string? Condition);

/// <summary>One <c>bpmn:process</c>.</summary>
public sealed record BpmnProcess(
    string? Id,
    string? Name,
    bool IsExecutable,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<SequenceFlow> Flows);

/// <summary>The parsed content of one BPMN file.</summary>
public sealed record BpmnDefinitions(IReadOnlyList<BpmnProcess> Processes);

/// <summary>One validation or parse problem, located by element id when known.</summary>
public sealed record BpmnIssue(string? ElementId, string Code, string Message);

/// <summary>The file was rejected; <see cref="Issues"/> lists every problem found (FR-DEP-004).</summary>
public sealed record BpmnInvalid(IReadOnlyList<BpmnIssue> Issues);

/// <summary>Result of reading BPMN XML: a valid model or the full list of issues (CC-008).</summary>
public union BpmnReadResult(BpmnDefinitions, BpmnInvalid);

/// <summary>One <c>flowd:input</c> or <c>flowd:output</c>: an expression (or literal) and the variable it is written to.</summary>
public sealed record IoMapping(string Source, string Target);
