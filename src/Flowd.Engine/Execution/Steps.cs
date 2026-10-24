using Flowd.Bpmn.Model;

namespace Flowd.Engine.Execution;

/// <summary>Create and activate an element in a scope (arrived over <see cref="IncomingFlowId"/>).</summary>
internal sealed record ActivateElement(string ElementId, long ScopeKey, string? IncomingFlowId, bool Triggered = false);

/// <summary>Run the completion logic of an element instance (output mappings, then take flows).</summary>
internal sealed record CompleteElement(long ElementInstanceKey);

/// <summary>Re-run the step that failed with an incident (FR-INT-003).</summary>
internal sealed record ReactivateElement(long ElementInstanceKey);

/// <summary>Units of work queued while a command runs; processed FIFO (FR-INT-001).</summary>
internal union Step(ActivateElement, CompleteElement, ReactivateElement);

/// <summary>Maps model elements to the element-type names stored in <c>element_instance.element_type</c>.</summary>
public static class ElementTypes
{
    public static string NameOf(FlowNode node) => node switch
    {
        StartEvent => "startEvent",
        EndEvent => "endEvent",
        IntermediateCatchEvent => "intermediateCatchEvent",
        BoundaryEvent => "boundaryEvent",
        ReceiveTask => "receiveTask",
        EventBasedGateway => "eventBasedGateway",
        ServiceTask task => task.BpmnType,
        ScriptTask => "scriptTask",
        SubProcess => "subProcess",
        InclusiveGateway => "inclusiveGateway",
        ManualTask => "manualTask",
        ExclusiveGateway => "exclusiveGateway",
        ParallelGateway => "parallelGateway",
        UnsupportedNode unsupported => unsupported.ElementType,
    };
}
