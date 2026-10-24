using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Engine.Results;
using Flowd.Expressions;

namespace Flowd.Application.Deployment;

/// <summary>Deploy-time syntax check of every expression in a process (FR-DEP-004: "expressions parse").</summary>
internal static class ExpressionChecker
{
    public static IEnumerable<FailureDetail> Check(string resourceName, BpmnProcess process)
    {
        foreach (var (owner, text) in ExpressionsOf(process))
        {
            if (FeelEvaluator.CheckSyntax(text) is { } error)
            {
                yield return new FailureDetail($"{resourceName}#{owner}", BpmnIssueCodes.InvalidExpression, $"Invalid expression '{text}': {error.Message}");
            }
        }
    }

    private static IEnumerable<(string Owner, string Text)> ExpressionsOf(BpmnProcess process)
    {
        foreach (var flow in process.AllFlows().Where(f => !string.IsNullOrWhiteSpace(f.Condition)))
        {
            yield return (flow.Id, flow.Condition!);
        }

        foreach (var node in process.AllNodes())
        {
            foreach (var text in ExpressionsOf(node))
            {
                yield return (node.Id, text);
            }
        }
    }

    private static IEnumerable<string> ExpressionsOf(FlowNode node)
    {
        switch (node)
        {
            case ServiceTask task:
                foreach (var text in new[] { task.TaskType, task.Retries }.Concat(task.Inputs.Select(m => m.Source)).Concat(task.Outputs.Select(m => m.Source)))
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        yield return text;
                    }
                }

                break;
            case SubProcess sub:
                foreach (var text in sub.Inputs.Concat(sub.Outputs).Select(m => m.Source).Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    yield return text;
                }

                break;
            case ScriptTask { Expression: { } expression } when !string.IsNullOrWhiteSpace(expression):
                yield return expression;
                break;
        }
    }
}
