using Flowd.Bpmn.Model;
using Flowd.Engine.Handlers;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>
/// Creates the jobs of execution and task listeners (FR-LST-001). A listener job is an ordinary job with the header <see cref="Header"/>
/// naming the event. It never blocks the element, cannot change variables, and a failure never raises an incident: use it for
/// notifications, audit and side effects. Delivery is at-least-once.
/// </summary>
internal static class ListenerJobs
{
    public const string Header = "_flowdListener";

    public static bool IsListener(JobRecord job) => job.CustomHeaders.ContainsKey(Header);

    public static void Fire(StepContext context, FlowNode node, ElementInstance instance, string @event)
    {
        foreach (var listener in node.Listeners.Where(l => string.Equals(l.Event, @event, StringComparison.Ordinal)))
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) { [Header] = @event, ["listenerEvent"] = @event };
            JobFactory.Create(context, instance, listener.JobType, context.Options.DefaultRetries, headers);
        }
    }

    public static void Fire(StepContext context, ElementInstance instance, string @event)
    {
        if (context.State.Graph.Process.AllNodes().FirstOrDefault(n => string.Equals(n.Id, instance.ElementId, StringComparison.Ordinal)) is { } node)
        {
            Fire(context, node, instance, @event);
        }
    }
}
