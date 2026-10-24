using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.State;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Execution;

/// <summary>
/// Multi-instance activities (FR-EXEC-012). A <em>body</em> element instance owns one child instance per input item; children live in
/// the body's scope. Progress is kept in the internal variable <c>_flowdMi</c> of the body scope so it survives between commands.
/// </summary>
internal static class MultiInstance
{
    public const string BodyType = "multiInstanceBody";
    private const string StateVariable = "_flowdMi";

    public static LoopCharacteristics? LoopOf(FlowNode node) => node switch
    {
        ServiceTask task => task.Loop,
        SubProcess sub => sub.Loop,
        UserTask user => user.Loop,
        CallActivity call => call.Loop,
        _ => null, // non-union: only these activities can be multi-instance
    };

    public static bool IsBody(ElementInstance element) => string.Equals(element.ElementType, BodyType, StringComparison.Ordinal);

    /// <summary>Evaluates the input collection and starts the children (all at once, or the first when sequential).</summary>
    public static void ActivateBody(StepContext context, FlowNode node, ElementInstance body, LoopCharacteristics loop)
    {
        var result = context.Evaluate(loop.InputCollection, body.ScopeKey);
        if (result is not EvaluationSuccess { Value: ListValue list })
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Input collection of '{node.Id}' did not evaluate to a list.", body);
            return;
        }

        var input = new JsonArray([.. list.Items.Select(ExpressionJson.ToJson)]);
        context.MarkActive(body);
        if (input.Count == 0)
        {
            WriteOutput(context, loop, body, []);
            context.Complete(body);
            context.TakeAllOutgoing(body);
            return;
        }

        var started = loop.IsSequential ? 1 : input.Count;
        context.WriteInternal(body.Key, StateVariable, new JsonObject
        {
            ["total"] = input.Count,
            ["started"] = started,
            ["done"] = 0,
            ["input"] = input,
            ["output"] = new JsonArray([.. Enumerable.Repeat<JsonNode?>(null, input.Count)]),
        });
        for (var index = 0; index < started; index++)
        {
            context.Enqueue(new ActivateElement(node.Id, body.Key, null, LoopIndex: index));
        }
    }

    /// <summary>Gives a child its iteration item as a local variable before the activity starts.</summary>
    public static bool PrepareChild(StepContext context, ElementInstance child, LoopCharacteristics loop)
    {
        var state = ReadState(context, child.ScopeKey);
        var item = state["input"]!.AsArray()[child.LoopCounter!.Value];
        if (context.WriteVariable(child.Key, loop.InputElement, item, propagate: false) is { } error)
        {
            context.RaiseIncident(error.IncidentType, error.Message, child);
            return false;
        }

        return true;
    }

    /// <summary>A child finished: collect its output element, start the next sequential child or complete the body.</summary>
    public static void ChildCompleted(StepContext context, ElementInstance child)
    {
        if (!context.State.Elements.TryGetValue(child.ScopeKey, out var body) || !body.IsLive)
        {
            return;
        }

        var node = context.State.Graph.Node(child.ElementId);
        var loop = LoopOf(node)!;
        var state = ReadState(context, body.Key);
        if (loop.OutputElement is { } outputElement)
        {
            var output = context.Evaluate(outputElement, child.Key);
            if (output is not EvaluationSuccess success)
            {
                context.RaiseIncident(IncidentTypes.IoMappingError, $"Output element of '{node.Id}' failed: {MessageOf(output)}", body);
                return;
            }

            state["output"]!.AsArray()[child.LoopCounter!.Value] = ExpressionJson.ToJson(success.Value);
        }

        context.ClearLocalScopeNow(child);
        var done = state["done"]!.GetValue<int>() + 1;
        state["done"] = done;
        var total = state["total"]!.GetValue<int>();
        var conditionMet = loop.CompletionCondition is not null && CompletionConditionMet(context, loop, body, state);
        if (done >= total || conditionMet)
        {
            if (conditionMet)
            {
                CancelRemaining(context, body, child);
            }

            WriteOutput(context, loop, body, state["output"]!.AsArray());
            context.ClearLocalScope(body);
            context.Complete(body);
            context.TakeAllOutgoing(body);
            return;
        }

        var started = state["started"]!.GetValue<int>();
        if (loop.IsSequential && started < total)
        {
            state["started"] = started + 1;
            context.Enqueue(new ActivateElement(node.Id, body.Key, null, LoopIndex: started));
        }

        context.WriteInternal(body.Key, StateVariable, state);
    }

    /// <summary>Evaluates the completion condition with the output collected so far visible under the output collection name.</summary>
    private static bool CompletionConditionMet(StepContext context, LoopCharacteristics loop, ElementInstance body, JsonObject state)
    {
        if (loop.OutputCollection is { } name && context.WriteVariable(body.Key, name, state["output"]!.DeepClone(), propagate: false) is not null)
        {
            return false;
        }

        return context.Evaluate(loop.CompletionCondition!, body.Key) is EvaluationSuccess { Value: bool met } && met;
    }

    private static void CancelRemaining(StepContext context, ElementInstance body, ElementInstance finished)
    {
        foreach (var sibling in context.State.LiveElements().Where(e => e.ScopeKey == body.Key && e.Key != finished.Key).ToList())
        {
            TriggerCommands.TerminateTree(context, sibling);
        }
    }

    private static void WriteOutput(StepContext context, LoopCharacteristics loop, ElementInstance body, JsonArray output)
    {
        if (loop.OutputCollection is { } name && context.WriteVariable(body.ScopeKey, name, output, propagate: true) is { } error)
        {
            context.RaiseIncident(error.IncidentType, error.Message, body);
        }
    }

    private static JsonObject ReadState(StepContext context, long bodyKey) =>
        (JsonObject)context.State.Variables.InScope(bodyKey)[StateVariable]!;

    private static string MessageOf(EvaluationResult result) => result is EvaluationError error ? error.Message : string.Empty;
}
