using System.Globalization;
using Flowd.Application.Timers;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;

namespace Flowd.Application.Deployment;

/// <summary>The timers and message subscriptions a new process version starts with (FR-TMR-005, FR-MSG-004).</summary>
internal sealed record StartTriggerPlan(IReadOnlyList<StartTimerDraft> Timers, IReadOnlyList<StartMessageDraft> Messages);

/// <summary>Works out the start-event triggers of a process at deployment time.</summary>
internal static class StartTriggerPlanner
{
    public static StartTriggerPlan Plan(string resourceName, BpmnProcess process, DateTimeOffset now, IKeyGenerator keys, List<FailureDetail> problems)
    {
        var timers = new List<StartTimerDraft>();
        var messages = new List<StartMessageDraft>();
        foreach (var start in process.Nodes.OfType<StartEvent>())
        {
            switch (start.Definition)
            {
                case TimerEventDefinition timer:
                    PlanTimer(resourceName, start, timer, now, keys, timers, problems);
                    break;
                case SignalEventDefinition signal:
                    messages.Add(new StartMessageDraft(keys.NextKey(), start.Id, signal.Name, IsSignal: true));
                    break;
                case MessageEventDefinition message:
                    messages.Add(new StartMessageDraft(keys.NextKey(), start.Id, message.Name));
                    break;
            }
        }

        return new StartTriggerPlan(timers, messages);
    }

    private static void PlanTimer(string resourceName, StartEvent start, TimerEventDefinition timer, DateTimeOffset now, IKeyGenerator keys, List<StartTimerDraft> timers, List<FailureDetail> problems)
    {
        var text = timer.Expression;
        if (FeelEvaluator.IsDynamic(text))
        {
            var result = FeelEvaluator.Evaluate(text, new ExpressionContext(new Dictionary<string, Flowd.Expressions.Values.ExpressionValue>(StringComparer.Ordinal), now));
            text = result is EvaluationSuccess { Value: string value } ? value : string.Empty;
        }

        if (!IsoTimer.TryComputeDue(timer.Kind, text, now, out var due, out var cycle))
        {
            problems.Add(new FailureDetail($"{resourceName}#{start.Id}", BpmnIssueCodes.InvalidTimer, string.Create(CultureInfo.InvariantCulture, $"Start timer '{timer.Expression}' is not a valid ISO 8601 {timer.Kind}.")));
            return;
        }

        timers.Add(new StartTimerDraft(keys.NextKey(), start.Id, due, cycle?.Repetitions, cycle?.Interval));
    }
}
