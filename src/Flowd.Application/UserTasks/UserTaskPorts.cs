using System.Text.Json.Nodes;
using Flowd.Application.Execution;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;

namespace Flowd.Application.UserTasks;

public sealed record UserTaskView(
    long Key,
    long InstanceKey,
    long ElementInstanceKey,
    string ElementId,
    string? Assignee,
    IReadOnlyList<string> CandidateGroups,
    IReadOnlyList<string> CandidateUsers,
    string? DueDate,
    string? FollowUpDate,
    int Priority,
    string? FormKey,
    string State,
    DateTimeOffset CreatedAt);

public sealed record UserTaskFilter(string? Assignee, string? CandidateGroup, string? CandidateUser, string? State, long? InstanceKey, int? MinPriority = null, DateTimeOffset? DueBefore = null);

public sealed record UserTaskPage(IReadOnlyList<UserTaskView> Items, long? NextCursor);

/// <summary>Port: user task read side (API-011).</summary>
public interface IUserTaskQueries
{
    Task<UserTaskPage> ListAsync(string tenantId, UserTaskFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken);

    Task<long?> InstanceOfUserTaskAsync(string tenantId, long taskKey, CancellationToken cancellationToken);
}

/// <summary>Claim, unclaim, assign and complete user tasks through the same locked command pipeline as everything else (API-011).</summary>
public sealed class UserTaskService(IUserTaskQueries queries, CommandProcessor processor)
{
    public Task<Result<ProcessedCommand>> ClaimAsync(string tenantId, long taskKey, string user, CancellationToken cancellationToken) =>
        RunAsync(tenantId, taskKey, new ClaimUserTask(taskKey, user), cancellationToken);

    public Task<Result<ProcessedCommand>> UnclaimAsync(string tenantId, long taskKey, CancellationToken cancellationToken) =>
        RunAsync(tenantId, taskKey, new UnclaimUserTask(taskKey), cancellationToken);

    public Task<Result<ProcessedCommand>> AssignAsync(string tenantId, long taskKey, string user, CancellationToken cancellationToken) =>
        RunAsync(tenantId, taskKey, new AssignUserTask(taskKey, user), cancellationToken);

    public Task<Result<ProcessedCommand>> CompleteAsync(string tenantId, long taskKey, JsonObject? variables, string? user, CancellationToken cancellationToken) =>
        RunAsync(tenantId, taskKey, new CompleteUserTask(taskKey, variables, user), cancellationToken);

    private async Task<Result<ProcessedCommand>> RunAsync(string tenantId, long taskKey, EngineCommand command, CancellationToken cancellationToken)
    {
        var instanceKey = await queries.InstanceOfUserTaskAsync(tenantId, taskKey, cancellationToken).ConfigureAwait(false);
        return instanceKey is { } key
            ? await processor.ExecuteAsync(tenantId, key, command, cancellationToken).ConfigureAwait(false)
            : new Failure(new NotFound($"User task {taskKey} does not exist."));
    }
}
