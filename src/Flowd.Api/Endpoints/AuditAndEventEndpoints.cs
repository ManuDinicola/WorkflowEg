using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Flowd.Api.Security;
using Flowd.Application.Audit;
using Flowd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>The audit log (admins) and the server-sent event stream of lifecycle events (FR-SEC-005, FR-API-010).</summary>
public static class AuditAndEventEndpoints
{
    private const int MaxPageSize = 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public static IEndpointRouteBuilder MapAuditAndEventEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1");
        group.MapGet("/audit", ListAuditAsync).RequireAuthorization(Policies.Audit);
        group.MapGet("/events/stream", StreamAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    private static async Task<IResult> ListAuditAsync(ClaimsPrincipal user, IAuditLog audit, int? pageSize, long? after, CancellationToken cancellationToken)
    {
        var page = await audit.ListAsync(user.TenantId(), Math.Clamp(pageSize ?? 100, 1, MaxPageSize), after, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = page.Items.Select(e => new { seq = e.Seq.ToString(CultureInfo.InvariantCulture), occurredAt = e.OccurredAt, actor = e.Actor, method = e.Method, path = e.Path, status = e.Status }),
            nextCursor = page.NextCursor?.ToString(CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Streams lifecycle events as server-sent events. Resume with <c>after</c> or the <c>Last-Event-ID</c> header.</summary>
    private static async Task StreamAsync(HttpContext context, ClaimsPrincipal user, IEventFeed feed, long? after, CancellationToken cancellationToken)
    {
        var cursor = after ?? (long.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : 0);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var tenant = user.TenantId();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var events = await feed.ReadAsync(tenant, cursor, 100, cancellationToken).ConfigureAwait(false);
                foreach (var e in events)
                {
                    var data = JsonSerializer.Serialize(new { seq = e.Seq.ToString(CultureInfo.InvariantCulture), processInstanceKey = e.InstanceKey.ToString(CultureInfo.InvariantCulture), type = e.EventType, elementId = e.ElementId, payload = e.Payload, occurredAt = e.OccurredAt });
                    await context.Response.WriteAsync($"id: {e.Seq}\nevent: {e.EventType}\ndata: {data}\n\n", cancellationToken).ConfigureAwait(false);
                    cursor = e.Seq;
                }

                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (events.Count == 0)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client disconnected.
        }
    }
}
