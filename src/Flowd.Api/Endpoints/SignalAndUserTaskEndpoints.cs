using System.Security.Claims;
using Flowd.Api.Security;
using Flowd.Api.Errors;
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
        int? pageSize,
        string? after,
        CancellationToken cancellationToken)
    {
        var page = await queries.ListAsync(
            user.TenantId(),
            new UserTaskFilter(assignee, candidateGroup, candidateUser, state, InstanceEndpoints.TryKey(processInstanceKey)),
            Math.Clamp(pageSize ?? 100, 1, MaxPageSize),
            InstanceEndpoints.TryKey(after),
            cancellationToken).ConfigureAwait(false);
        var items = page.Items.Select(t => new UserTaskDto(
            InstanceEndpoints.Id(t.Key), InstanceEndpoints.Id(t.InstanceKey), t.ElementId, t.Assignee, t.CandidateGroups, t.CandidateUsers, t.DueDate, t.FormKey, t.State, t.CreatedAt));
        return Results.Ok(new PageDto<UserTaskDto>([.. items], page.NextCursor is { } c ? InstanceEndpoints.Id(c) : null));
    }

    private static Task<IResult> ClaimAsync(ClaimsPrincipal user, string key, ClaimUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.ClaimAsync(user.TenantId(), k, request.User, cancellationToken));

    private static Task<IResult> UnclaimAsync(ClaimsPrincipal user, string key, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.UnclaimAsync(user.TenantId(), k, cancellationToken));

    private static Task<IResult> AssignAsync(ClaimsPrincipal user, string key, AssignUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.AssignAsync(user.TenantId(), k, request.User, cancellationToken));

    private static Task<IResult> CompleteAsync(ClaimsPrincipal user, string key, CompleteUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.CompleteAsync(user.TenantId(), k, request.Variables, request.User, cancellationToken));
}
