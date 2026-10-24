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
}
