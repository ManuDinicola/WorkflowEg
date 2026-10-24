using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.Handlers;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>Commands the application sends back to a parent instance when its child instance progressed (FR-EXEC-011).</summary>
internal static class CallActivityCommands
{
    public static CommandRejected? Complete(StepContext context, CompleteCallActivity command)
    {
        if (Waiting(context, command.ElementInstanceKey, out var activity) is { } rejection)
        {
            return rejection;
        }

        context.WriteInternal(activity.Key, CallActivityHandler.ChildResultVariable, command.Variables.DeepClone());
        context.Enqueue(new CompleteElement(activity.Key));
        return null;
    }

    public static CommandRejected? Fail(StepContext context, FailCallActivity command)
    {
        if (Waiting(context, command.ElementInstanceKey, out var activity) is { } rejection)
        {
            return rejection;
        }

        context.RaiseIncident(IncidentTypes.CalledElementError, command.Message, activity);
        return null;
    }

    public static CommandRejected? ThrowError(StepContext context, ThrowFromCallActivity command)
    {
        if (Waiting(context, command.ElementInstanceKey, out var activity) is { } rejection)
        {
            return rejection;
        }

        if (!ErrorPropagation.Throw(context, activity, command.ErrorCode, null))
        {
            context.RaiseIncident(IncidentTypes.UnhandledErrorEvent, $"The called process threw '{command.ErrorCode}' and nothing handles it.", activity);
        }

        return null;
    }

    private static CommandRejected? Waiting(StepContext context, long key, out ElementInstance activity)
    {
        activity = null!; // only read when no rejection is returned
        if (context.State.State != InstanceState.Active)
        {
            return Rejections.Conflict(InstanceCommands.InstanceNotActive, "The instance is not active.");
        }

        if (!context.State.Elements.TryGetValue(key, out var found) || !found.IsLive || !string.Equals(found.ElementType, "callActivity", StringComparison.Ordinal))
        {
            return Rejections.Conflict("CALL_ACTIVITY_NOT_WAITING", "The call activity is no longer waiting for its child instance.");
        }

        activity = found;
        return null;
    }
}
