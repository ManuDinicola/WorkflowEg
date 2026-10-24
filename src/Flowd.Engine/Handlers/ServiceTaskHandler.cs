using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions;
using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Values;

namespace Flowd.Engine.Handlers;

/// <summary>Job-worker service task (FR-EXEC-006, FR-JOB-003, FR-JOB-005): activation creates a job, completion maps its result.</summary>
public sealed class ServiceTaskHandler : ElementHandler<ServiceTask>
{
    public override void Activate(StepContext context, ServiceTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (context.State.Jobs.Values.Any(j => j.ElementInstanceKey == instance.Key && j.State is JobState.Activatable or JobState.Activated))
        {
            return;
        }

        if (!TryResolveTaskSettings(context, element, instance, out var type, out var retries)
            || !MappingSupport.TryApplyInputs(context, element.Inputs, element.Id, instance))
        {
            return;
        }

        context.MarkActive(instance);
        CreateJob(context, element, instance, type, retries);
    }

    public override void Complete(StepContext context, ServiceTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.BeginCompleting(instance);
        var result = context.State.Jobs.Values
            .Where(j => j.ElementInstanceKey == instance.Key && j.State == JobState.Completed)
            .OrderByDescending(j => j.Key)
            .Select(j => j.ResultVariables)
            .FirstOrDefault() ?? [];

        if (!MappingSupport.TryApplyOutputs(context, element.Outputs, element.PropagateAllOutputs, result, element.Id, instance))
        {
            return;
        }

        context.ClearLocalScope(instance);
        context.Complete(instance);
        context.TakeAllOutgoing(instance);
    }

    private static bool TryResolveTaskSettings(StepContext context, ServiceTask task, ElementInstance instance, out string type, out int retries)
    {
        type = string.Empty;
        retries = context.Options.DefaultRetries;
        var typeResult = context.Evaluate(task.TaskType ?? string.Empty, instance.ScopeKey);
        if (typeResult is not EvaluationSuccess { Value: string text } || string.IsNullOrWhiteSpace(text))
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Job type of '{task.Id}' did not evaluate to a non-empty string.{MappingSupport.Describe(typeResult)}", instance);
            return false;
        }

        type = text;
        if (task.Retries is null)
        {
            return true;
        }

        var retriesResult = context.Evaluate(task.Retries, instance.ScopeKey);
        if (!TryReadInt(retriesResult, out retries) || retries < 0)
        {
            context.RaiseIncident(IncidentTypes.ExtractValueError, $"Retries of '{task.Id}' did not evaluate to a non-negative integer.{MappingSupport.Describe(retriesResult)}", instance);
            return false;
        }

        return true;
    }

    private static bool TryReadInt(EvaluationResult result, out int value)
    {
        value = 0;
        switch (result)
        {
            case EvaluationSuccess { Value: decimal d } when d == decimal.Truncate(d) && d >= 0 && d <= int.MaxValue:
                value = (int)d;
                return true;
            case EvaluationSuccess { Value: string s }:
                return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            case EvaluationSuccess or EvaluationError:
                return false;
        }

        return false;
    }

    private static void CreateJob(StepContext context, ServiceTask task, ElementInstance instance, string type, int retries)
    {
        var job = new JobRecord
        {
            Key = context.NextKey(),
            InstanceKey = context.State.InstanceKey,
            ElementInstanceKey = instance.Key,
            ElementId = task.Id,
            Type = type,
            State = JobState.Activatable,
            Retries = retries,
            CustomHeaders = task.Headers,
            Variables = context.State.Variables.Visible(instance.Key),
            TenantId = context.State.TenantId,
            CreatedAt = context.Now,
        };
        context.State.Jobs[job.Key] = job;
        context.Emit(new JobChanged(job.Copy()));
        context.Record(HistoryEventTypes.JobCreated, instance, new JsonObject { ["jobKey"] = job.Key.ToString(CultureInfo.InvariantCulture), ["type"] = type });
    }
}
