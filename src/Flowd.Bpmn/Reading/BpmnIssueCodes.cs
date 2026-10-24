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
}
