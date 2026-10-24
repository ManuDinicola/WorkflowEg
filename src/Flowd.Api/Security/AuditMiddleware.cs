using Flowd.Application.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowd.Api.Security;

/// <summary>
/// Records every mutating API call (POST, PUT, PATCH, DELETE) with caller, tenant, path and result in the audit log (FR-SEC-005).
/// A failing audit write is logged and never fails the request. Variable values and bodies are not recorded.
/// </summary>
public sealed partial class AuditMiddleware(RequestDelegate next, TimeProvider time, ILogger<AuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IAuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);
        await next(context).ConfigureAwait(false);
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        {
            return;
        }

        var actor = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name ?? "unnamed" : "anonymous";
        var entry = new AuditEntry(0, time.GetUtcNow(), context.User.TenantId(), actor, context.Request.Method, context.Request.Path.Value ?? string.Empty, context.Response.StatusCode);
        try
        {
            await audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit log write failed.")]
    private partial void LogFailed(Exception exception);
}
