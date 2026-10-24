using Flowd.Bpmn.Model;
using Flowd.Engine.Execution;
using Flowd.Engine.State;

namespace Flowd.Engine.Handlers;

/// <summary>Behavior of one BPMN element kind (CC-003). Adding an element adds a handler; the interpreter loop stays closed.</summary>
public interface IElementHandler
{
    Type ElementType { get; }

    /// <summary>The token arrived: do the element's work, wait, or complete.</summary>
    void Activate(StepContext context, FlowNode element, ElementInstance instance);

    /// <summary>Finish the element (after a wait state ended) and let the token move on.</summary>
    void Complete(StepContext context, FlowNode element, ElementInstance instance);
}

/// <summary>Typed form of <see cref="IElementHandler"/>.</summary>
public interface IElementHandler<in TElement> : IElementHandler
    where TElement : FlowNode
{
    void Activate(StepContext context, TElement element, ElementInstance instance);

    void Complete(StepContext context, TElement element, ElementInstance instance);
}

/// <summary>Base class: bridges the untyped interpreter calls to typed handlers. Default completion passes the token on.</summary>
public abstract class ElementHandler<TElement> : IElementHandler<TElement>
    where TElement : FlowNode
{
    public Type ElementType => typeof(TElement);

    public abstract void Activate(StepContext context, TElement element, ElementInstance instance);

    public virtual void Complete(StepContext context, TElement element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Complete(instance);
        context.TakeAllOutgoing(instance);
    }

    void IElementHandler.Activate(StepContext context, FlowNode element, ElementInstance instance) =>
        Activate(context, (TElement)element, instance);

    void IElementHandler.Complete(StepContext context, FlowNode element, ElementInstance instance) =>
        Complete(context, (TElement)element, instance);
}

/// <summary>Finds the handler for a model element (CC-003).</summary>
public sealed class ElementHandlerRegistry
{
    private readonly Dictionary<Type, IElementHandler> _handlers;

    public ElementHandlerRegistry(IEnumerable<IElementHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToDictionary(h => h.ElementType);
    }

    public static ElementHandlerRegistry CreateDefault() => new(
    [
        new StartEventElementHandler(),
        new EndEventElementHandler(),
        new ManualTaskHandler(),
        new ServiceTaskHandler(),
        new ExclusiveGatewayHandler(),
        new ParallelGatewayHandler(),
        new InclusiveGatewayHandler(),
        new SubProcessHandler(),
        new ScriptTaskHandler(),
        new IntermediateCatchEventElementHandler(),
        new BoundaryEventElementHandler(),
        new ReceiveTaskHandler(),
        new EventBasedGatewayHandler(),
        new IntermediateThrowEventElementHandler(),
        new UserTaskElementHandler(),
        new CallActivityHandler(),
    ]);

    public IEnumerable<IReevaluatingHandler> Reevaluating() => _handlers.Values.OfType<IReevaluatingHandler>();

    public IElementHandler For(FlowNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _handlers.TryGetValue(element.GetType(), out var handler)
            ? handler
            : throw new InvalidOperationException($"No handler registered for {element.GetType().Name} '{element.Id}'.");
    }
}
