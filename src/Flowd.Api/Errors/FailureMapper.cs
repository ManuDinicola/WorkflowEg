using Flowd.Engine.Results;
using Microsoft.AspNetCore.Http;

namespace Flowd.Api.Errors;

/// <summary>The one place where <see cref="Failure"/> cases become RFC 9457 responses with stable codes (UNI-006, API-R4).</summary>
public static class FailureMapper
{
    public static IResult ToProblem(Failure failure) => failure switch
    {
        ValidationFailure v => Problem(StatusCodes.Status400BadRequest, v.Details is null ? "INVALID_ARGUMENT" : "DEPLOYMENT_INVALID", "Invalid request", v.Message, v.Details),
        NotFound n => Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", n.Message),
        Conflict c => Problem(StatusCodes.Status409Conflict, c.Code, "Conflict", c.Message),
        PermissionDenied p => Problem(StatusCodes.Status403Forbidden, "PERMISSION_DENIED", "Permission denied", p.Message),
        RateLimited r => Problem(StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "Rate limited", r.Message),
        Unavailable u => Problem(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "Unavailable", u.Message),
    };

    private static IResult Problem(int status, string code, string title, string detail, IReadOnlyList<FailureDetail>? details = null)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code };
        if (details is { Count: > 0 })
        {
            extensions["errors"] = details.Select(d => new { target = d.Target, code = d.Code, message = d.Message });
        }

        return Results.Problem(title: title, detail: detail, statusCode: status, extensions: extensions);
    }
}
