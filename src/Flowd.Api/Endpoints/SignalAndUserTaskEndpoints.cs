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
    private const string DefaultTenant = "";
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapSignalAndUserTaskEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/signals", BroadcastAsync);
        var tasks = routes.MapGroup("/v1/user-tasks");
        tasks.MapGet(string.Empty, ListAsync);
        tasks.MapPost("/{key}/claim", ClaimAsync);
        tasks.MapPost("/{key}/unclaim", UnclaimAsync);
        tasks.MapPost("/{key}/assign", AssignAsync);
        tasks.MapPost("/{key}/complete", CompleteAsync);
        return routes;
    }

    private static async Task<IResult> BroadcastAsync(BroadcastSignalRequestDto request, ISignalBroadcaster signals, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("A signal needs a name.")));
        }

        var triggered = await signals.BroadcastAsync(DefaultTenant, request.Name, request.Variables, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BroadcastSignalResponseDto(triggered));
    }

    private static async Task<IResult> ListAsync(
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
            DefaultTenant,
            new UserTaskFilter(assignee, candidateGroup, candidateUser, state, InstanceEndpoints.TryKey(processInstanceKey)),
            Math.Clamp(pageSize ?? 100, 1, MaxPageSize),
            InstanceEndpoints.TryKey(after),
            cancellationToken).ConfigureAwait(false);
        var items = page.Items.Select(t => new UserTaskDto(
            InstanceEndpoints.Id(t.Key), InstanceEndpoints.Id(t.InstanceKey), t.ElementId, t.Assignee, t.CandidateGroups, t.CandidateUsers, t.DueDate, t.FormKey, t.State, t.CreatedAt));
        return Results.Ok(new PageDto<UserTaskDto>([.. items], page.NextCursor is { } c ? InstanceEndpoints.Id(c) : null));
    }

    private static Task<IResult> ClaimAsync(string key, ClaimUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.ClaimAsync(DefaultTenant, k, request.User, cancellationToken));

    private static Task<IResult> UnclaimAsync(string key, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.UnclaimAsync(DefaultTenant, k, cancellationToken));

    private static Task<IResult> AssignAsync(string key, AssignUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.AssignAsync(DefaultTenant, k, request.User, cancellationToken));

    private static Task<IResult> CompleteAsync(string key, CompleteUserTaskRequestDto request, UserTaskService tasks, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => tasks.CompleteAsync(DefaultTenant, k, request.Variables, request.User, cancellationToken));
}
