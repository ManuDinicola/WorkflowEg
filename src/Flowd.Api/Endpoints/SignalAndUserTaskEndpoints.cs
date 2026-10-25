using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Messaging;
using Flowd.Application.UserTasks;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Signals (API-009) and native user tasks (API-011).</summary>
public static class SignalAndUserTaskEndpoints
{
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapSignalAndUserTaskEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/signals", BroadcastAsync).RequireAuthorization(Policies.Publish);
        var tasks = routes.MapGroup("/v1/user-tasks");
        tasks.MapGet(string.Empty, ListAsync).RequireAuthorization(Policies.UserTask);
        tasks.MapPost("/{key}/claim", ClaimAsync).RequireAuthorization(Policies.UserTask);
        tasks.MapPost("/{key}/unclaim", UnclaimAsync).RequireAuthorization(Policies.UserTask);
        tasks.MapPost("/{key}/assign", AssignAsync).RequireAuthorization(Policies.UserTask);
        tasks.MapPost("/{key}/complete", CompleteAsync).RequireAuthorization(Policies.UserTask);
        return routes;
    }

    private static async Task<IResult> BroadcastAsync(ClaimsPrincipal user, BroadcastSignalRequestDto request, ISignalBroadcaster signals, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("A signal needs a name.")));
        }

        var triggered = await signals.BroadcastAsync(user.TenantId(), request.Name, request.Variables, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BroadcastSignalResponseDto(triggered));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        IUserTaskQueries queries,
        string? assignee,
        string? candidateGroup,
        string? candidateUser,
        string? state,
        string? processInstanceKey,
        int? minPriority,
        DateTimeOffset? dueBefore,
        int? pageSize,
        string? after,
        CancellationToken cancellationToken)
    {
        var page = await queries.ListAsync(
            user.TenantId(),
            new UserTaskFilter(assignee, candidateGroup, candidateUser, state, InstanceEndpoints.TryKey(processInstanceKey), minPriority, dueBefore),
            Math.Clamp(pageSize ?? 100, 1, MaxPageSize),
            InstanceEndpoints.TryKey(after),
            cancellationToken).ConfigureAwait(false);
        var items = page.Items.Where(t => user.MayWorkOn(t.Assignee, t.CandidateGroups, t.CandidateUsers)).Select(t => new UserTaskDto(
            InstanceEndpoints.Id(t.Key), InstanceEndpoints.Id(t.InstanceKey), t.ElementId, t.Assignee, t.CandidateGroups, t.CandidateUsers, t.DueDate, t.FollowUpDate, t.Priority, t.FormKey, t.State, t.CreatedAt));
        return Results.Ok(new PageDto<UserTaskDto>([.. items], page.NextCursor is { } c ? InstanceEndpoints.Id(c) : null));
    }

    /// <summary>Callers with groups reach only the tasks they are assignee or candidate of; others answer 404 as if the task did not exist (FR-SEC-009).</summary>
    internal static async Task<bool> MayReachAsync(ClaimsPrincipal user, string key, IUserTaskQueries queries, CancellationToken cancellationToken)
    {
        if (user.Groups().Count == 0)
        {
            return true;
        }

        var task = InstanceEndpoints.TryKey(key) is { } k ? await queries.GetAsync(user.TenantId(), k, cancellationToken).ConfigureAwait(false) : null;
        return task is not null && user.MayWorkOn(task.Assignee, task.CandidateGroups, task.CandidateUsers);
    }

    private static string AsCaller(ClaimsPrincipal user, string? requested) => user.Groups().Count > 0 ? user.Identity?.Name ?? string.Empty : requested ?? string.Empty;

    private static async Task<IResult> ClaimAsync(ClaimsPrincipal user, string key, ClaimUserTaskRequestDto request, UserTaskService tasks, IUserTaskQueries queries, CancellationToken cancellationToken) =>
        !await MayReachAsync(user, key, queries, cancellationToken).ConfigureAwait(false)
            ? InstanceEndpoints.NotFound("User task", key)
            : await InstanceEndpoints.RunAsync(key, k => tasks.ClaimAsync(user.TenantId(), k, AsCaller(user, request.User), cancellationToken)).ConfigureAwait(false);

    private static async Task<IResult> UnclaimAsync(ClaimsPrincipal user, string key, UserTaskService tasks, IUserTaskQueries queries, CancellationToken cancellationToken) =>
        !await MayReachAsync(user, key, queries, cancellationToken).ConfigureAwait(false)
            ? InstanceEndpoints.NotFound("User task", key)
            : await InstanceEndpoints.RunAsync(key, k => tasks.UnclaimAsync(user.TenantId(), k, cancellationToken)).ConfigureAwait(false);

    private static async Task<IResult> AssignAsync(ClaimsPrincipal user, string key, AssignUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken)
    {
        if (user.Groups().Count > 0)
        {
            return FailureMapper.ToProblem(new Failure(new PermissionDenied("A caller restricted to its own tasks can claim tasks but not assign them to others.")));
        }

        return await InstanceEndpoints.RunAsync(key, k => tasks.AssignAsync(user.TenantId(), k, request.User, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<IResult> CompleteAsync(ClaimsPrincipal user, string key, CompleteUserTaskRequestDto request, UserTaskService tasks, IUserTaskQueries queries, CancellationToken cancellationToken) =>
        !await MayReachAsync(user, key, queries, cancellationToken).ConfigureAwait(false)
            ? InstanceEndpoints.NotFound("User task", key)
            : await InstanceEndpoints.RunAsync(key, k => tasks.CompleteAsync(user.TenantId(), k, request.Variables, user.Groups().Count > 0 ? AsCaller(user, null) : request.User, cancellationToken)).ConfigureAwait(false);
}
