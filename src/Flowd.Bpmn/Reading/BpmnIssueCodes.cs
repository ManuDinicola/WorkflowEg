namespace Flowd.Bpmn.Reading;

/// <summary>Stable machine codes for <see cref="Model.BpmnIssue"/> (CC-012).</summary>
public static class BpmnIssueCodes
{
    public const string MalformedXml = "MALFORMED_XML";
    public const string DtdNotAllowed = "DTD_NOT_ALLOWED";
    public const string NoProcess = "NO_PROCESS";
    public const string MissingProcessId = "MISSING_PROCESS_ID";
    public const string DuplicateProcessId = "DUPLICATE_PROCESS_ID";
    public const string NotExecutable = "NOT_EXECUTABLE";
    public const string DuplicateId = "DUPLICATE_ID";
    public const string MissingId = "MISSING_ID";
    public const string UnsupportedElement = "UNSUPPORTED_ELEMENT";
    public const string DanglingReference = "DANGLING_REFERENCE";
    public const string NoStartEvent = "NO_START_EVENT";
    public const string MissingTaskType = "MISSING_TASK_TYPE";
    public const string MissingCondition = "MISSING_CONDITION";
    public const string InvalidDefaultFlow = "INVALID_DEFAULT_FLOW";
    public const string ConditionNotAllowed = "CONDITION_NOT_ALLOWED";
    public const string InvalidMapping = "INVALID_MAPPING";
    public const string InvalidExpression = "INVALID_EXPRESSION";
    public const string InvalidTimer = "INVALID_TIMER";
    public const string InvalidBoundary = "INVALID_BOUNDARY_EVENT";
    public const string InvalidEventDefinition = "INVALID_EVENT_DEFINITION";
    public const string InvalidGatewayTarget = "INVALID_EVENT_GATEWAY";
    public const string InvalidLoop = "INVALID_MULTI_INSTANCE";
    public const string InvalidCall = "INVALID_CALL_ACTIVITY";
    public const string InvalidEventSubProcess = "INVALID_EVENT_SUBPROCESS";
}
