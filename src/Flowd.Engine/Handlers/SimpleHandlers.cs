using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;

namespace Flowd.Engine.Handlers;

/// <summary>None start event (FR-EXEC-001): becomes active and immediately passes the token on (FR-EXEC-002).</summary>
public sealed class StartEventElementHandler : ElementHandler<StartEvent>
{
    public override void Activate(StepContext context, StartEvent element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.MarkActive(instance);
        Complete(context, element, instance);
    }
}

/// <summary>Pass-through manual task.</summary>
public sealed class ManualTaskHandler : ElementHandler<ManualTask>
{
    public override void Activate(StepContext context, ManualTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.MarkActive(instance);
        Complete(context, element, instance);
    }
}

/// <summary>None and terminate end events (FR-EXEC-008). Scope completion is decided by the interpreter once the queue drains.</summary>
public sealed class EndEventElementHandler : ElementHandler<EndEvent>
{
    public override void Activate(StepContext context, EndEvent element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.MarkActive(instance);
        if (element.Error is { } error)
        {
            if (!ErrorPropagation.Throw(context, instance, error.Code ?? string.Empty, null))
            {
                context.RaiseIncident(IncidentTypes.UnhandledErrorEvent, $"No error handler for code '{error.Code}'.", instance);
            }

            return;
        }

        if (element.MessageJobType is { } jobType)
        {
            if (!JobFactory.HasOpenJob(context, instance))
            {
                JobFactory.Create(context, instance, jobType, context.Options.DefaultRetries, new Dictionary<string, string>(StringComparer.Ordinal));
            }

            return;
        }

        if (element.IsTerminate)
        {
            TerminateSiblings(context, instance);
        }

        context.Complete(instance);
    }

    private static void TerminateSiblings(StepContext context, ElementInstance self)
    {
        var siblings = context.State.LiveElements().Where(e => e.Key != self.Key && e.ScopeKey == self.ScopeKey).ToList();
        foreach (var sibling in siblings.OrderByDescending(e => e.Key))
        {
            context.Terminate(sibling);
        }
    }
}
