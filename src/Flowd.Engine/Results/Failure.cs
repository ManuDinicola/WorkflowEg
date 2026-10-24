namespace Flowd.Engine.Results;

/// <summary>One located problem inside a <see cref="ValidationFailure"/>, e.g. a BPMN issue on an element.</summary>
public sealed record FailureDetail(string? Target, string Code, string Message);

/// <summary>Expected failure: input did not pass validation. <see cref="Details"/> lists every problem found.</summary>
public sealed record ValidationFailure(string Message, IReadOnlyList<FailureDetail>? Details = null);

/// <summary>Expected failure: the addressed entity does not exist (or is hidden by tenancy).</summary>
public sealed record NotFound(string Message);

/// <summary>Expected failure: the request conflicts with current state, e.g. a job that is not activated.</summary>
public sealed record Conflict(string Code, string Message);

/// <summary>Expected failure: the caller lacks permission.</summary>
public sealed record PermissionDenied(string Message);

/// <summary>Expected failure: the caller exceeded a rate limit.</summary>
public sealed record RateLimited(string Message);

/// <summary>Expected failure: a dependency is temporarily unavailable.</summary>
public sealed record Unavailable(string Message, string Code = "UNAVAILABLE");

/// <summary>Closed set of expected failures, mapped to API error codes in exactly one place (UNI-002).</summary>
public union Failure(ValidationFailure, NotFound, Conflict, PermissionDenied, RateLimited, Unavailable);

/// <summary>Result of an operation that can fail in an expected way (CC-008, UNI-002).</summary>
public union Result<T>(T, Failure);
