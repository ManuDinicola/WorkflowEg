using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;

namespace Flowd.Engine.Handlers;

/// <summary>Intermediate catch event: opens its timer or message subscription and waits (section 6.1, FR-TMR-001, FR-MSG-002).</summary>
public sealed class IntermediateCatchEventElementHandler : ElementHandler<IntermediateCatchEvent>
{
    public override void Activate(StepContext context, IntermediateCatchEvent element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (context.ActivationFromTrigger)
        {
            context.MarkActive(instance);
            Complete(context, element, instance);
            return;
        }

        if (!Triggers.IsOpen(context, instance, element.Id) && !Triggers.Open(context, instance, element.Id, element.Definition, TriggerKind.Catch))
        {
            return;
        }

        context.MarkActive(instance);
    }
}

/// <summary>A boundary event that fired: it becomes active and passes the token on (FR-EXEC-013).</summary>
public sealed class BoundaryEventElementHandler : ElementHandler<BoundaryEvent>
{
    public override void Activate(StepContext context, BoundaryEvent element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.MarkActive(instance);
        Complete(context, element, instance);
    }
}

/// <summary>Receive task: waits for a message (FR-MSG-004). Boundary events are opened by the interpreter.</summary>
public sealed class ReceiveTaskHandler : ElementHandler<ReceiveTask>
{
    public override void Activate(StepContext context, ReceiveTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (context.ActivationFromTrigger)
        {
            context.MarkActive(instance);
            Complete(context, element, instance);
            return;
        }

        if (!Triggers.IsOpen(context, instance, element.Id) && !Triggers.Open(context, instance, element.Id, element.Message, TriggerKind.Catch))
        {
            return;
        }

        context.MarkActive(instance);
    }
}

/// <summary>Event-based gateway: opens the triggers of every target; the first to fire wins and the others are cancelled (FR-EXEC-014).</summary>
public sealed class EventBasedGatewayHandler : ElementHandler<EventBasedGateway>
{
    public override void Activate(StepContext context, EventBasedGateway element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        foreach (var flow in context.State.Graph.Outgoing(element.Id))
        {
            var definition = context.State.Graph.Node(flow.TargetRef) switch
            {
                IntermediateCatchEvent catchEvent => catchEvent.Definition,
                ReceiveTask receive => receive.Message,
                _ => null, // non-union: the validator only allows catch events and receive tasks here
            };
            if (definition is null || Triggers.IsOpen(context, instance, flow.TargetRef))
            {
                continue;
            }

            if (!Triggers.Open(context, instance, flow.TargetRef, definition, TriggerKind.EventGateway))
            {
                context.CloseTriggersOf(instance);
                return;
            }
        }

        context.MarkActive(instance);
    }
}
