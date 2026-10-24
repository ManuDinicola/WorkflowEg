namespace Flowd.Bpmn.Model;

/// <summary>A sequence flow. <see cref="Condition"/> is the raw expression text, if any.</summary>
public sealed record SequenceFlow(string Id, string SourceRef, string TargetRef, string? Condition);

/// <summary>One <c>bpmn:process</c>. Sub-processes nest their own nodes and flows.</summary>
public sealed record BpmnProcess(
    string? Id,
    string? Name,
    bool IsExecutable,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<SequenceFlow> Flows)
{
    /// <summary>Every node of the process including those inside sub-processes, depth first.</summary>
    public IEnumerable<FlowNode> AllNodes() => Flatten(Nodes);

    /// <summary>Every sequence flow of the process including those inside sub-processes.</summary>
    public IEnumerable<SequenceFlow> AllFlows() => Flows.Concat(AllNodes().OfType<SubProcess>().SelectMany(s => s.Flows));

    private static IEnumerable<FlowNode> Flatten(IEnumerable<FlowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is SubProcess sub)
            {
                foreach (var inner in Flatten(sub.Nodes))
                {
                    yield return inner;
                }
            }
        }
    }
}

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
