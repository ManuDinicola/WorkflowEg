using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Bpmn.Model;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.State;
using Flowd.Expressions.Evaluation;

namespace Flowd.Engine.Handlers;

/// <summary>Creates jobs for every element that hands work to an external worker (FR-EXEC-006, FR-JOB-009).</summary>
internal static class JobFactory
{
    public static JobRecord Create(StepContext context, ElementInstance instance, string type, int retries, IReadOnlyDictionary<string, string> headers)
    {
        var job = new JobRecord
        {
            Key = context.NextKey(),
            InstanceKey = context.State.InstanceKey,
            ElementInstanceKey = instance.Key,
            ElementId = instance.ElementId,
            Type = type,
            State = JobState.Activatable,
            Retries = retries,
            CustomHeaders = headers,
            Variables = context.State.Variables.Visible(instance.Key),
            TenantId = context.State.TenantId,
            CreatedAt = context.Now,
        };
        context.State.Jobs[job.Key] = job;
        context.Emit(new JobChanged(job.Copy()));
        context.Record(HistoryEventTypes.JobCreated, instance, new JsonObject { ["jobKey"] = job.Key.ToString(CultureInfo.InvariantCulture), ["type"] = type });
        return job;
    }

    public static bool HasOpenJob(StepContext context, ElementInstance instance) =>
        context.State.Jobs.Values.Any(j => j.ElementInstanceKey == instance.Key && j.State is JobState.Activatable or JobState.Activated);
}

/// <summary>Intermediate throw events: none, message (creates a job) or signal (broadcast after the commit).</summary>
public sealed class IntermediateThrowEventElementHandler : ElementHandler<IntermediateThrowEvent>
{
    public override void Activate(StepContext context, IntermediateThrowEvent element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (element.JobType is { } type)
        {
            if (!JobFactory.HasOpenJob(context, instance))
            {
                JobFactory.Create(context, instance, type, context.Options.DefaultRetries, new Dictionary<string, string>(StringComparer.Ordinal));
            }

            context.MarkActive(instance);
            return;
        }

        if (element.Definition is LinkEventDefinition link)
        {
            context.MarkActive(instance);
            context.Complete(instance);
            if (context.State.Graph.LinkCatch(element.Id, link.Name) is { } target)
            {
                context.Enqueue(new ActivateElement(target.Id, instance.ScopeKey, null));
            }

            return;
        }

        if (element.Definition is EscalationEventDefinition escalation)
        {
            context.MarkActive(instance);
            ErrorPropagation.Escalate(context, instance, escalation.Code ?? string.Empty, null);
            if (context.State.Elements[instance.Key].IsLive)
            {
                Complete(context, element, instance);
            }

            return;
        }

        if (element.Definition is SignalEventDefinition signal)
        {
            context.Emit(new SignalThrown(signal.Name, []));
            context.Record(HistoryEventTypes.SignalThrownEvent, instance, new JsonObject { ["signal"] = signal.Name });
        }

        context.MarkActive(instance);
        Complete(context, element, instance);
    }
}

/// <summary>Native user task (FR-EXEC-006 analogue for humans, API-011): creates a task record and waits.</summary>
public sealed class UserTaskElementHandler : ElementHandler<UserTask>
{
    public override void Activate(StepContext context, UserTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        if (context.State.UserTasks.Values.Any(t => t.ElementInstanceKey == instance.Key && t.State == UserTaskState.Created))
        {
            return;
        }

        if (!MappingSupport.TryApplyInputs(context, element.Inputs, element.Id, instance)
            || !TryResolve(context, element.Assignee, instance, out var assignee)
            || !TryResolve(context, element.DueDate, instance, out var dueDate)
            || !TryResolve(context, element.FollowUpDate, instance, out var followUpDate)
            || !TryResolve(context, element.FormKey, instance, out var formKey))
        {
            return;
        }

        var task = new UserTaskRecord
        {
            Key = context.NextKey(),
            InstanceKey = context.State.InstanceKey,
            ElementInstanceKey = instance.Key,
            ElementId = element.Id,
            Assignee = assignee,
            CandidateGroups = element.CandidateGroups,
            CandidateUsers = element.CandidateUsers,
            DueDate = dueDate,
            FollowUpDate = followUpDate,
            Priority = element.Priority,
            FormKey = formKey,
            State = UserTaskState.Created,
            TenantId = context.State.TenantId,
            CreatedAt = context.Now,
        };
        context.State.UserTasks[task.Key] = task;
        context.Emit(new UserTaskChanged(task.Copy()));
        context.Record(HistoryEventTypes.UserTaskCreated, instance, new JsonObject { ["userTaskKey"] = task.Key.ToString(CultureInfo.InvariantCulture) });
        context.MarkActive(instance);
    }

    public override void Complete(StepContext context, UserTask element, ElementInstance instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        context.BeginCompleting(instance);
        var result = context.State.UserTasks.Values
            .Where(t => t.ElementInstanceKey == instance.Key && t.State == UserTaskState.Completed)
            .OrderByDescending(t => t.Key)
            .Select(t => t.ResultVariables)
            .FirstOrDefault() ?? [];
        if (!MappingSupport.TryApplyOutputs(context, element.Outputs, element.PropagateAllOutputs, result, element.Id, instance))
        {
            return;
        }

        context.ClearLocalScope(instance);
        context.Complete(instance);
        context.TakeAllOutgoing(instance);
    }

    private static bool TryResolve(StepContext context, string? text, ElementInstance instance, out string? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        var result = context.Evaluate(text, instance.ScopeKey);
        if (result is EvaluationSuccess { Value: string resolved })
        {
            value = resolved;
            return true;
        }

        context.RaiseIncident(IncidentTypes.ExtractValueError, $"User task setting '{text}' did not evaluate to a string.{MappingSupport.Describe(result)}", instance);
        return false;
    }
}
