using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>Claim, unclaim, assign and complete for native user tasks (API-011, T-UT-01).</summary>
internal static class UserTaskCommands
{
    public const string AlreadyClaimed = "USER_TASK_ALREADY_CLAIMED";
    public const string NotActive = "USER_TASK_NOT_ACTIVE";
    public const string AssignedToOther = "USER_TASK_ASSIGNED_TO_OTHER";

    public static CommandRejected? Claim(StepContext context, ClaimUserTask command)
    {
        if (Open(context, command.TaskKey, out var task) is { } rejection)
        {
            return rejection;
        }

        if (task.Assignee is { } holder && !string.Equals(holder, command.User, StringComparison.Ordinal))
        {
            return Rejections.Conflict(AlreadyClaimed, $"The task is already claimed by '{holder}'.");
        }

        Assign(context, task, command.User);
        return null;
    }

    public static CommandRejected? Unclaim(StepContext context, UnclaimUserTask command)
    {
        if (Open(context, command.TaskKey, out var task) is { } rejection)
        {
            return rejection;
        }

        Assign(context, task, null);
        return null;
    }

    public static CommandRejected? Assign(StepContext context, AssignUserTask command)
    {
        if (Open(context, command.TaskKey, out var task) is { } rejection)
        {
            return rejection;
        }

        Assign(context, task, command.User);
        return null;
    }

    public static CommandRejected? Complete(StepContext context, CompleteUserTask command)
    {
        if (Open(context, command.TaskKey, out var task) is { } rejection)
        {
            return rejection;
        }

        if (command.User is not null && task.Assignee is { } holder && !string.Equals(holder, command.User, StringComparison.Ordinal))
        {
            return Rejections.Conflict(AssignedToOther, $"The task is assigned to '{holder}'.");
        }

        if (command.Variables?.Any(p => p.Key.StartsWith(StepContext.ReservedPrefix, StringComparison.Ordinal)) == true)
        {
            return Rejections.Invalid($"Variable names must not start with '{StepContext.ReservedPrefix}'.");
        }

        task.State = UserTaskState.Completed;
        task.ResultVariables = Rejections.CloneOrNull(command.Variables) ?? [];
        context.Emit(new UserTaskChanged(task.Copy()));
        context.Record(HistoryEventTypes.UserTaskCompleted, context.State.Elements.GetValueOrDefault(task.ElementInstanceKey));
        context.Enqueue(new CompleteElement(task.ElementInstanceKey));
        return null;
    }

    private static CommandRejected? Open(StepContext context, long key, out UserTaskRecord task)
    {
        task = null!; // only read when no rejection is returned
        if (!context.State.UserTasks.TryGetValue(key, out var found))
        {
            return Rejections.NotFound($"User task {key.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        if (found.State != UserTaskState.Created)
        {
            return Rejections.Conflict(NotActive, $"The user task is {found.State}.");
        }

        task = found;
        return null;
    }

    private static void Assign(StepContext context, UserTaskRecord task, string? user)
    {
        task.Assignee = user;
        context.Emit(new UserTaskChanged(task.Copy()));
        context.Record(HistoryEventTypes.UserTaskClaimed, context.State.Elements.GetValueOrDefault(task.ElementInstanceKey), new JsonObject { ["assignee"] = user });
    }
}
