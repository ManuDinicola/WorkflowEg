using System.Security;

namespace Flowd.Conformance.Tests.Support;

/// <summary>Tiny builders for conformance fixtures (inline BPMN XML; larger fixtures live in <c>testdata/bpmn</c>).</summary>
public static class Bpmn
{
    public static string Process(string id, params string[] elements) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            {string.Join("\n    ", elements)}
          </bpmn:process>
        </bpmn:definitions>
        """;

    public static string Start(string id = "Start") => $"<bpmn:startEvent id=\"{id}\"/>";

    public static string End(string id = "End") => $"<bpmn:endEvent id=\"{id}\"/>";

    public static string TerminateEnd(string id) => $"<bpmn:endEvent id=\"{id}\"><bpmn:terminateEventDefinition/></bpmn:endEvent>";

    public static string Manual(string id) => $"<bpmn:manualTask id=\"{id}\"/>";

    public static string Service(string id, string type, string? retries = null, string inner = "") =>
        $"<bpmn:serviceTask id=\"{id}\"><bpmn:extensionElements><flowd:taskDefinition type=\"{SecurityElement.Escape(type)}\"{(retries is null ? string.Empty : $" retries=\"{SecurityElement.Escape(retries)}\"")}/>{inner}</bpmn:extensionElements></bpmn:serviceTask>";

    public static string Mapping(string inputs, string outputs, bool propagateAll = true) =>
        $"<flowd:ioMapping{(propagateAll ? string.Empty : " propagateAll=\"false\"")}>{inputs}{outputs}</flowd:ioMapping>";

    public static string Input(string source, string target) => $"<flowd:input source=\"{SecurityElement.Escape(source)}\" target=\"{target}\"/>";

    public static string Output(string source, string target) => $"<flowd:output source=\"{SecurityElement.Escape(source)}\" target=\"{target}\"/>";

    public static string Exclusive(string id, string? defaultFlow = null) =>
        $"<bpmn:exclusiveGateway id=\"{id}\"{(defaultFlow is null ? string.Empty : $" default=\"{defaultFlow}\"")}/>";

    public static string Parallel(string id) => $"<bpmn:parallelGateway id=\"{id}\"/>";

    public static string Flow(string id, string from, string to, string? condition = null) => condition is null
        ? $"<bpmn:sequenceFlow id=\"{id}\" sourceRef=\"{from}\" targetRef=\"{to}\"/>"
        : $"<bpmn:sequenceFlow id=\"{id}\" sourceRef=\"{from}\" targetRef=\"{to}\"><bpmn:conditionExpression>{SecurityElement.Escape(condition)}</bpmn:conditionExpression></bpmn:sequenceFlow>";

    public static string Inclusive(string id, string? defaultFlow = null) =>
        $"<bpmn:inclusiveGateway id=\"{id}\"{(defaultFlow is null ? string.Empty : $" default=\"{defaultFlow}\"")}/>";

    public static string Script(string id, string expression, string resultVariable) =>
        $"<bpmn:scriptTask id=\"{id}\"><bpmn:extensionElements><flowd:script expression=\"{SecurityElement.Escape(expression)}\" resultVariable=\"{resultVariable}\"/></bpmn:extensionElements></bpmn:scriptTask>";

    public static string SubProcess(string id, string mapping, params string[] inner) =>
        $"<bpmn:subProcess id=\"{id}\"><bpmn:extensionElements>{mapping}</bpmn:extensionElements>{string.Join(string.Empty, inner)}</bpmn:subProcess>";

    public static string SendTask(string id, string type) =>
        $"<bpmn:sendTask id=\"{id}\"><bpmn:extensionElements><flowd:taskDefinition type=\"{type}\"/></bpmn:extensionElements></bpmn:sendTask>";

    /// <summary>Process whose definitions element also declares messages (<c>bpmn:message</c> with a correlation key).</summary>
    public static string ProcessWithMessages(string id, string messages, params string[] elements) =>
        Process(id, elements).Replace("<bpmn:process ", messages + "<bpmn:process ", StringComparison.Ordinal);

    public static string MessageDef(string id, string name, string correlationKey = "= key") =>
        $"<bpmn:message id=\"{id}\" name=\"{name}\"><bpmn:extensionElements><flowd:subscription correlationKey=\"{SecurityElement.Escape(correlationKey)}\"/></bpmn:extensionElements></bpmn:message>";

    private static string TimerDef(string kind, string value) =>
        $"<bpmn:timerEventDefinition><bpmn:{kind}>{SecurityElement.Escape(value)}</bpmn:{kind}></bpmn:timerEventDefinition>";

    public static string CatchTimer(string id, string value, string kind = "timeDuration") =>
        $"<bpmn:intermediateCatchEvent id=\"{id}\">{TimerDef(kind, value)}</bpmn:intermediateCatchEvent>";

    public static string CatchMessage(string id, string messageRef) =>
        $"<bpmn:intermediateCatchEvent id=\"{id}\"><bpmn:messageEventDefinition messageRef=\"{messageRef}\"/></bpmn:intermediateCatchEvent>";

    public static string BoundaryTimer(string id, string attachedTo, string value, string kind = "timeDuration", bool cancel = true) =>
        $"<bpmn:boundaryEvent id=\"{id}\" attachedToRef=\"{attachedTo}\" cancelActivity=\"{(cancel ? "true" : "false")}\">{TimerDef(kind, value)}</bpmn:boundaryEvent>";

    public static string BoundaryMessage(string id, string attachedTo, string messageRef, bool cancel = true) =>
        $"<bpmn:boundaryEvent id=\"{id}\" attachedToRef=\"{attachedTo}\" cancelActivity=\"{(cancel ? "true" : "false")}\"><bpmn:messageEventDefinition messageRef=\"{messageRef}\"/></bpmn:boundaryEvent>";

    public static string ReceiveTask(string id, string messageRef) => $"<bpmn:receiveTask id=\"{id}\" messageRef=\"{messageRef}\"/>";

    public static string EventGateway(string id) => $"<bpmn:eventBasedGateway id=\"{id}\"/>";

    public static string StartTimer(string id, string value, string kind = "timeDuration") =>
        $"<bpmn:startEvent id=\"{id}\">{TimerDef(kind, value)}</bpmn:startEvent>";

    public static string StartMessage(string id, string messageRef) =>
        $"<bpmn:startEvent id=\"{id}\"><bpmn:messageEventDefinition messageRef=\"{messageRef}\"/></bpmn:startEvent>";

    public static string ErrorDef(string id, string code) => $"<bpmn:error id=\"{id}\" name=\"{id}\" errorCode=\"{code}\"/>";

    public static string SignalDef(string id, string name) => $"<bpmn:signal id=\"{id}\" name=\"{name}\"/>";

    public static string BoundaryError(string id, string attachedTo, string? errorRef = null) =>
        $"<bpmn:boundaryEvent id=\"{id}\" attachedToRef=\"{attachedTo}\"><bpmn:errorEventDefinition{(errorRef is null ? string.Empty : $" errorRef=\"{errorRef}\"")}/></bpmn:boundaryEvent>";

    public static string ErrorEnd(string id, string? errorRef = null) =>
        $"<bpmn:endEvent id=\"{id}\"><bpmn:errorEventDefinition{(errorRef is null ? string.Empty : $" errorRef=\"{errorRef}\"")}/></bpmn:endEvent>";

    public static string CatchSignal(string id, string signalRef) =>
        $"<bpmn:intermediateCatchEvent id=\"{id}\"><bpmn:signalEventDefinition signalRef=\"{signalRef}\"/></bpmn:intermediateCatchEvent>";

    public static string BoundarySignal(string id, string attachedTo, string signalRef, bool cancel = true) =>
        $"<bpmn:boundaryEvent id=\"{id}\" attachedToRef=\"{attachedTo}\" cancelActivity=\"{(cancel ? "true" : "false")}\"><bpmn:signalEventDefinition signalRef=\"{signalRef}\"/></bpmn:boundaryEvent>";

    public static string StartSignal(string id, string signalRef) =>
        $"<bpmn:startEvent id=\"{id}\"><bpmn:signalEventDefinition signalRef=\"{signalRef}\"/></bpmn:startEvent>";

    public static string ThrowSignal(string id, string signalRef) =>
        $"<bpmn:intermediateThrowEvent id=\"{id}\"><bpmn:signalEventDefinition signalRef=\"{signalRef}\"/></bpmn:intermediateThrowEvent>";

    public static string EscalationDef(string id, string code) => $"<bpmn:escalation id=\"{id}\" name=\"{id}\" escalationCode=\"{code}\"/>";

    public static string BoundaryEscalation(string id, string attachedTo, string? escalationRef = null, bool cancel = true) =>
        $"<bpmn:boundaryEvent id=\"{id}\" attachedToRef=\"{attachedTo}\" cancelActivity=\"{(cancel ? "true" : "false")}\"><bpmn:escalationEventDefinition{(escalationRef is null ? string.Empty : $" escalationRef=\"{escalationRef}\"")}/></bpmn:boundaryEvent>";

    public static string ThrowEscalation(string id, string? escalationRef = null) =>
        $"<bpmn:intermediateThrowEvent id=\"{id}\"><bpmn:escalationEventDefinition{(escalationRef is null ? string.Empty : $" escalationRef=\"{escalationRef}\"")}/></bpmn:intermediateThrowEvent>";

    public static string EscalationEnd(string id, string? escalationRef = null) =>
        $"<bpmn:endEvent id=\"{id}\"><bpmn:escalationEventDefinition{(escalationRef is null ? string.Empty : $" escalationRef=\"{escalationRef}\"")}/></bpmn:endEvent>";

    public static string ThrowLink(string id, string name) =>
        $"<bpmn:intermediateThrowEvent id=\"{id}\"><bpmn:linkEventDefinition name=\"{name}\"/></bpmn:intermediateThrowEvent>";

    public static string CatchLink(string id, string name) =>
        $"<bpmn:intermediateCatchEvent id=\"{id}\"><bpmn:linkEventDefinition name=\"{name}\"/></bpmn:intermediateCatchEvent>";

    public static string ThrowNone(string id) => $"<bpmn:intermediateThrowEvent id=\"{id}\"/>";

    public static string ThrowMessage(string id, string jobType) =>
        $"<bpmn:intermediateThrowEvent id=\"{id}\"><bpmn:messageEventDefinition/><bpmn:extensionElements><flowd:taskDefinition type=\"{jobType}\"/></bpmn:extensionElements></bpmn:intermediateThrowEvent>";

    public static string UserTask(string id, string assignment = "", string inner = "") =>
        $"<bpmn:userTask id=\"{id}\"><bpmn:extensionElements>{(assignment.Length == 0 ? string.Empty : $"<flowd:assignmentDefinition {assignment}/>")}{inner}</bpmn:extensionElements></bpmn:userTask>";

    private static string Loop(bool sequential, string inputCollection, string inputElement, string? outputCollection, string? outputElement) =>
        $"<bpmn:multiInstanceLoopCharacteristics isSequential=\"{(sequential ? "true" : "false")}\"><bpmn:extensionElements><flowd:loopCharacteristics inputCollection=\"{SecurityElement.Escape(inputCollection)}\" inputElement=\"{inputElement}\"{(outputCollection is null ? string.Empty : $" outputCollection=\"{outputCollection}\" outputElement=\"{SecurityElement.Escape(outputElement ?? string.Empty)}\"")}/></bpmn:extensionElements></bpmn:multiInstanceLoopCharacteristics>";

    public static string MiService(string id, string type, bool sequential, string inputCollection, string inputElement, string? outputCollection = null, string? outputElement = null) =>
        $"<bpmn:serviceTask id=\"{id}\"><bpmn:extensionElements><flowd:taskDefinition type=\"{type}\"/></bpmn:extensionElements>{Loop(sequential, inputCollection, inputElement, outputCollection, outputElement)}</bpmn:serviceTask>";

    public static string CallActivity(string id, string calledElement, string inner = "") =>
        $"<bpmn:callActivity id=\"{id}\" calledElement=\"{calledElement}\"><bpmn:extensionElements>{inner}</bpmn:extensionElements></bpmn:callActivity>";

    public static string CallFlags(bool propagateChild, bool propagateParent, string mappings = "") =>
        $"<flowd:calledElement propagateAllChildVariables=\"{(propagateChild ? "true" : "false")}\" propagateAllParentVariables=\"{(propagateParent ? "true" : "false")}\"/>{mappings}";

    public static string EventSubProcess(string id, string start, params string[] inner) =>
        $"<bpmn:subProcess id=\"{id}\" triggeredByEvent=\"true\">{start}{string.Join(string.Empty, inner)}</bpmn:subProcess>";

    public static string EspStartMessage(string id, string messageRef, bool interrupting) =>
        $"<bpmn:startEvent id=\"{id}\" isInterrupting=\"{(interrupting ? "true" : "false")}\"><bpmn:messageEventDefinition messageRef=\"{messageRef}\"/></bpmn:startEvent>";

    public static string EspStartTimer(string id, string value, bool interrupting) =>
        $"<bpmn:startEvent id=\"{id}\" isInterrupting=\"{(interrupting ? "true" : "false")}\"><bpmn:timerEventDefinition><bpmn:timeDuration>{value}</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:startEvent>";

    public static string EspStartError(string id, string errorRef) =>
        $"<bpmn:startEvent id=\"{id}\"><bpmn:errorEventDefinition errorRef=\"{errorRef}\"/></bpmn:startEvent>";
}
