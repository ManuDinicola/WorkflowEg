using System.Security.Claims;
using Flowd.Api.Security;
using Flowd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Reports over the history: instances over time, durations, incidents and decision usage (FR-UI-013).</summary>
public static class ReportEndpoints
{
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxHourlyRange = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/reports");
        group.MapGet("/instances", InstancesAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/durations", DurationsAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/incidents", IncidentsAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/decisions", DecisionsAsync).RequireAuthorization(Policies.Read);
        return routes;
    }

    /// <summary>The range of a report: <c>from</c> to <c>to</c> (default the last 30 days), at most 366 days.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, TimeProvider time)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - DefaultRange;
        return end - start > MaxRange ? (end - MaxRange, end) : (start, end);
    }

    private static async Task<IResult> InstancesAsync(ClaimsPrincipal user, IReportQueries reports, TimeProvider time, string? bpmnProcessId, DateTimeOffset? from, DateTimeOffset? to, string? bucket, CancellationToken cancellationToken)
    {
        var (start, end) = Range(from, to, time);
        var hourly = string.Equals(bucket, "hour", StringComparison.OrdinalIgnoreCase) && end - start <= MaxHourlyRange;
        var buckets = await reports.InstancesOverTimeAsync(user.TenantId(), bpmnProcessId, start, end, hourly, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { from = start, to = end, bucket = hourly ? "hour" : "day", buckets = buckets.Select(b => new { start = b.Start, started = b.Started, completed = b.Completed, terminated = b.Terminated }) });
    }

    private static async Task<IResult> DurationsAsync(ClaimsPrincipal user, IReportQueries reports, TimeProvider time, string? bpmnProcessId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (start, end) = Range(from, to, time);
        var d = await reports.DurationsAsync(user.TenantId(), bpmnProcessId, start, end, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { from = start, to = end, completedInstances = d.Count, averageSeconds = d.Average, medianSeconds = d.Median, p90Seconds = d.P90, p99Seconds = d.P99, maxSeconds = d.Max });
    }

    private static async Task<IResult> IncidentsAsync(ClaimsPrincipal user, IReportQueries reports, TimeProvider time, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (start, end) = Range(from, to, time);
        var rows = await reports.IncidentsAsync(user.TenantId(), start, end, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { from = start, to = end, types = rows.Select(r => new { errorType = r.ErrorType, raised = r.Raised, stillOpen = r.StillOpen }) });
    }

    private static async Task<IResult> DecisionsAsync(ClaimsPrincipal user, IReportQueries reports, TimeProvider time, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (start, end) = Range(from, to, time);
        var rows = await reports.DecisionsAsync(user.TenantId(), start, end, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { from = start, to = end, decisions = rows.Select(r => new { decisionId = r.DecisionId, evaluations = r.Evaluations, failures = r.Failures, averageMs = r.AverageMs }) });
    }
}
