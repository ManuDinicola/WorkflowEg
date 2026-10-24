using System.Text.RegularExpressions;
using Flowd.Application.Queries;
using Microsoft.AspNetCore.Http;

namespace Flowd.Api.Security;

/// <summary>
/// For an API key limited to certain processes (<c>flowd:processes</c>), every <c>/v1/process-instances/{key}...</c> call on an instance of
/// another process answers 404, as if it did not exist (FR-SEC-006). Unrestricted callers are not affected and cost nothing.
/// </summary>
public sealed partial class ProcessScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IInstanceQueries queries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queries);
        if (context.User.AllowedProcesses() is { } allowed
            && InstancePath().Match(context.Request.Path.Value ?? string.Empty) is { Success: true } match
            && long.TryParse(match.Groups["key"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var key)
            && await queries.GetInstanceAsync(context.User.TenantId(), key, context.RequestAborted).ConfigureAwait(false) is { } instance
            && !allowed.Contains(instance.BpmnProcessId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^/v1/process-instances/(?<key>\d+)(?:/|$)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex InstancePath();
}
