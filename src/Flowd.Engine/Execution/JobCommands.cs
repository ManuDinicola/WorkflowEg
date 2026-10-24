using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.State;

namespace Flowd.Engine.Execution;

/// <summary>Worker-facing job commands: complete, fail, throw an error, update retries (section 9, FR-CON-005).</summary>
internal static class JobCommands
{
    public const string JobNotActivatedCode = "JOB_NOT_ACTIVATED";

    public static CommandRejected? Complete(StepContext context, CompleteJob command)
    {
        if (Locate(context, command.JobKey) is not { } job)
        {
            return Rejections.NotFound($"Job {Rejections.Id(command.JobKey)} does not exist.");
        }

        var rejection = Reject(Check(job, command.Worker), job);
        if (rejection is not null)
        {
            return rejection;
        }

        if (command.Variables?.Any(p => p.Key.StartsWith(StepContext.ReservedPrefix, StringComparison.Ordinal)) == true)
        {
            return Rejections.Invalid($"Variable names must not start with '{StepContext.ReservedPrefix}'.");
        }

        job.State = JobState.Completed;
        job.ResultVariables = Rejections.CloneOrNull(command.Variables) ?? [];
        context.Emit(new JobChanged(job.Copy()));
        context.Record(HistoryEventTypes.JobCompleted, context.State.Elements[job.ElementInstanceKey]);
        context.Enqueue(new CompleteElement(job.ElementInstanceKey));
        return null;
    }

    public static CommandRejected? Fail(StepContext context, FailJob command)
    {
        if (command.Retries < 0)
        {
            return Rejections.Invalid("Retries must not be negative.");
        }

        if (Locate(context, command.JobKey) is not { } job)
        {
            return Rejections.NotFound($"Job {Rejections.Id(command.JobKey)} does not exist.");
        }

        var rejection = Reject(Check(job, command.Worker), job);
        if (rejection is not null)
        {
            return rejection;
        }

        var element = context.State.Elements[job.ElementInstanceKey];
        job.Retries = command.Retries;
        job.ErrorMessage = command.ErrorMessage;
        job.Worker = null;
        job.Deadline = null;
        if (command.Retries == 0)
        {
            job.State = JobState.Failed;
            context.Record(HistoryEventTypes.JobFailed, element);
            context.Emit(new JobChanged(job.Copy()));
            context.RaiseIncident(IncidentTypes.JobNoRetries, command.ErrorMessage ?? "Job failed with no retries left.", element, job.Key);
            return null;
        }

        job.State = JobState.Activatable;
        job.RetryBackoffUntil = command.RetryBackoff is { } backoff ? context.Now + backoff : null;
        context.Record(HistoryEventTypes.JobFailed, element);
        context.Emit(new JobChanged(job.Copy()));
        return null;
    }

    public static CommandRejected? ThrowError(StepContext context, ThrowJobError command)
    {
        if (Locate(context, command.JobKey) is not { } job)
        {
            return Rejections.NotFound($"Job {Rejections.Id(command.JobKey)} does not exist.");
        }

        var rejection = Reject(Check(job, command.Worker), job);
        if (rejection is not null)
        {
            return rejection;
        }

        var element = context.State.Elements[job.ElementInstanceKey];
        job.State = JobState.ErrorThrown;
        job.ErrorMessage = command.ErrorMessage;
        job.Worker = null;
        job.Deadline = null;
        context.Emit(new JobChanged(job.Copy()));
        context.Record(HistoryEventTypes.JobErrorThrown, element);
        if (!ErrorPropagation.Throw(context, element, command.ErrorCode, command.Variables))
        {
            context.RaiseIncident(
                IncidentTypes.UnhandledErrorEvent,
                $"No error handler for code '{command.ErrorCode}'. {command.ErrorMessage}".Trim(),
                element,
                job.Key);
        }

        return null;
    }

    public static CommandRejected? UpdateRetries(StepContext context, UpdateJobRetries command)
    {
        if (command.Retries < 0)
        {
            return Rejections.Invalid("Retries must not be negative.");
        }

        if (!context.State.Jobs.TryGetValue(command.JobKey, out var job))
        {
            return Rejections.NotFound($"Job {Rejections.Id(command.JobKey)} does not exist.");
        }

        if (job.State is JobState.Completed or JobState.Canceled)
        {
            return Rejections.Conflict(JobNotActivatedCode, $"Job is {job.State}.");
        }

        job.Retries = command.Retries;
        context.Emit(new JobChanged(job.Copy()));
        context.Record(HistoryEventTypes.JobRetriesUpdated, context.State.Elements.GetValueOrDefault(job.ElementInstanceKey));
        return null;
    }

    private static JobRecord? Locate(StepContext context, long jobKey) =>
        context.State.Jobs.GetValueOrDefault(jobKey);

    private static JobCompletionOutcome Check(JobRecord job, string? worker)
    {
        if (job.State == JobState.Completed)
        {
            return new JobAlreadyCompleted();
        }

        if (job.State != JobState.Activated)
        {
            return new JobNotActivated();
        }

        return worker is not null && !string.Equals(worker, job.Worker, StringComparison.Ordinal) ? new JobStale() : new JobCompletable();
    }

    private static CommandRejected? Reject(JobCompletionOutcome outcome, JobRecord job) => outcome switch
    {
        JobCompletable => null,
        JobAlreadyCompleted => Rejections.Conflict(JobNotActivatedCode, "The job is already completed."),
        JobNotActivated => Rejections.Conflict(JobNotActivatedCode, $"The job is {job.State}, not activated."),
        JobStale => Rejections.Conflict(JobNotActivatedCode, "The job is now held by another worker."),
    };
}

/// <summary>Operator command that re-runs the step behind an incident (FR-INC-002, FR-INT-003).</summary>
internal static class IncidentCommands
{
    public static CommandRejected? Resolve(StepContext context, ResolveIncident command)
    {
        var state = context.State;
        if (!state.Incidents.TryGetValue(command.IncidentKey, out var incident))
        {
            return Rejections.NotFound($"Incident {Rejections.Id(command.IncidentKey)} does not exist.");
        }

        if (incident.State != IncidentState.Open)
        {
            return Rejections.Conflict("INCIDENT_NOT_OPEN", "The incident is already resolved.");
        }

        var job = incident.JobKey is { } key ? state.Jobs.GetValueOrDefault(key) : null;
        if (job is { Retries: 0 } && job.State == JobState.Failed)
        {
            return Rejections.Invalid("Update the job's retries to a value above zero before resolving the incident.");
        }

        incident.State = IncidentState.Resolved;
        incident.ResolvedAt = context.Now;
        context.Emit(new IncidentChanged(incident.Copy()));
        context.Record(HistoryEventTypes.IncidentResolved, incident.ElementInstanceKey is { } e ? state.Elements.GetValueOrDefault(e) : null);

        if (job is { State: JobState.Failed or JobState.ErrorThrown })
        {
            job.State = JobState.Activatable;
            job.RetryBackoffUntil = null;
            context.Emit(new JobChanged(job.Copy()));
        }
        else if (incident.ElementInstanceKey is { } elementKey)
        {
            context.Enqueue(new ReactivateElement(elementKey));
        }

        return null;
    }
}
