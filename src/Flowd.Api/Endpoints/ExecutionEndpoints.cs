using System.Globalization;
using Flowd.Api.Errors;
using Flowd.Application.Execution;
using Flowd.Application.Queries;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Process instances, history and incidents (API-003..006, API-010, API-012).</summary>
public static class InstanceEndpoints
{
    private const string DefaultTenant = "";
    private const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1");
        group.MapPost("/process-instances", StartAsync);
        group.MapGet("/process-instances", ListAsync);
        group.MapGet("/process-instances/{key}", GetAsync);
        group.MapGet("/process-instances/{key}/variables", GetVariablesAsync);
        group.MapPost("/process-instances/{key}/cancel", CancelAsync);
        group.MapPut("/process-instances/{key}/variables", SetVariablesAsync);
        group.MapGet("/process-instances/{key}/history", HistoryAsync);
        group.MapGet("/incidents", ListIncidentsAsync);
        group.MapPost("/incidents/{key}/resolve", ResolveIncidentAsync);
        return routes;
    }

    private static async Task<IResult> StartAsync(StartInstanceRequestDto request, StartInstanceUseCase useCase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BpmnProcessId))
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("bpmnProcessId is required.")));
        }

        var result = await useCase.ExecuteAsync(
            new StartInstanceRequest(DefaultTenant, request.BpmnProcessId, request.Version, request.Variables, request.BusinessKey, request.StartElementId),
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            StartedInstance started => Results.Ok(new StartInstanceResponseDto(Id(started.InstanceKey), Id(started.DefinitionKey), started.Version, started.BpmnProcessId)),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    private static async Task<IResult> GetAsync(string key, IInstanceQueries queries, CancellationToken cancellationToken)
    {
        var instance = TryKey(key) is { } k ? await queries.GetInstanceAsync(DefaultTenant, k, cancellationToken).ConfigureAwait(false) : null;
        return instance is null ? NotFound("Process instance", key) : Results.Ok(ToDto(instance));
    }

    private static async Task<IResult> GetVariablesAsync(string key, IInstanceQueries queries, CancellationToken cancellationToken)
    {
        var variables = TryKey(key) is { } k ? await queries.GetVariablesAsync(DefaultTenant, k, cancellationToken).ConfigureAwait(false) : null;
        return variables is null ? NotFound("Process instance", key) : Results.Ok(variables);
    }

    private static async Task<IResult> ListAsync(
        IInstanceQueries queries,
        string? state,
        string? bpmnProcessId,
        string? businessKey,
        DateTimeOffset? startedAfter,
        DateTimeOffset? startedBefore,
        int? pageSize,
        string? after,
        CancellationToken cancellationToken)
    {
        var page = await queries.ListInstancesAsync(
            DefaultTenant,
            new InstanceFilter(state, bpmnProcessId, businessKey, startedAfter, startedBefore),
            Math.Clamp(pageSize ?? 100, 1, MaxPageSize),
            TryKey(after),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PageDto<InstanceDto>([.. page.Items.Select(ToDto)], page.NextCursor is { } c ? Id(c) : null));
    }

    private static Task<IResult> CancelAsync(string key, InstanceCommandService commands, CancellationToken cancellationToken) =>
        RunAsync(key, k => commands.CancelAsync(DefaultTenant, k, cancellationToken));

    private static Task<IResult> SetVariablesAsync(string key, SetVariablesRequestDto request, InstanceCommandService commands, CancellationToken cancellationToken) =>
        RunAsync(key, k => commands.SetVariablesAsync(DefaultTenant, k, new SetVariables(request.Variables, TryKey(request.ScopeKey), request.Local), cancellationToken));

    private static async Task<IResult> HistoryAsync(string key, IInstanceQueries queries, int? pageSize, string? after, CancellationToken cancellationToken)
    {
        if (TryKey(key) is not { } instanceKey || await queries.GetInstanceAsync(DefaultTenant, instanceKey, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound("Process instance", key);
        }

        var page = await queries.GetHistoryAsync(DefaultTenant, instanceKey, Math.Clamp(pageSize ?? 100, 1, MaxPageSize), TryKey(after), cancellationToken).ConfigureAwait(false);
        var items = page.Items.Select(h => new HistoryEventDto(Id(h.Seq), h.EventType, h.ElementId, h.ElementInstanceKey is { } e ? Id(e) : null, h.Payload, h.OccurredAt));
        return Results.Ok(new PageDto<HistoryEventDto>([.. items], page.NextCursor is { } c ? Id(c) : null));
    }

    private static async Task<IResult> ListIncidentsAsync(IInstanceQueries queries, string? processInstanceKey, string? state, int? pageSize, string? after, CancellationToken cancellationToken)
    {
        var page = await queries.ListIncidentsAsync(DefaultTenant, TryKey(processInstanceKey), state, Math.Clamp(pageSize ?? 100, 1, MaxPageSize), TryKey(after), cancellationToken).ConfigureAwait(false);
        var items = page.Items.Select(i => new IncidentDto(
            Id(i.Key), Id(i.InstanceKey), i.ElementInstanceKey is { } e ? Id(e) : null, i.JobKey is { } j ? Id(j) : null, i.ErrorType, i.ErrorMessage, i.State, i.CreatedAt, i.ResolvedAt));
        return Results.Ok(new PageDto<IncidentDto>([.. items], page.NextCursor is { } c ? Id(c) : null));
    }

    private static Task<IResult> ResolveIncidentAsync(string key, InstanceCommandService commands, CancellationToken cancellationToken) =>
        RunAsync(key, k => commands.ResolveIncidentAsync(DefaultTenant, k, cancellationToken));

    internal static async Task<IResult> RunAsync(string key, Func<long, Task<Result<ProcessedCommand>>> run)
    {
        if (TryKey(key) is not { } parsed)
        {
            return NotFound("Resource", key);
        }

        var result = await run(parsed).ConfigureAwait(false);
        return result switch
        {
            ProcessedCommand => Results.NoContent(),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    internal static long? TryKey(string? key) =>
        long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    internal static string Id(long key) => key.ToString(CultureInfo.InvariantCulture);

    internal static IResult NotFound(string what, string key) =>
        FailureMapper.ToProblem(new Failure(new NotFound($"{what} '{key}' not found.")));

    private static InstanceDto ToDto(InstanceView v) => new(Id(v.Key), Id(v.DefinitionKey), v.BpmnProcessId, v.Version, v.State, v.StartedAt, v.EndedAt, v.BusinessKey);
}

/// <summary>Worker-facing job endpoints (API-007, API-008).</summary>
public static class JobEndpoints
{
    private const string DefaultTenant = "";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1/jobs");
        group.MapPost("/activate", ActivateAsync);
        group.MapPost("/{key}/complete", CompleteAsync);
        group.MapPost("/{key}/fail", FailAsync);
        group.MapPost("/{key}/error", ThrowErrorAsync);
        group.MapPatch("/{key}", UpdateAsync);
        return routes;
    }

    private static async Task<IResult> ActivateAsync(ActivateJobsRequestDto request, Application.Jobs.JobActivationService activation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Worker) || request.MaxJobs < 1 || request.TimeoutMs < 1)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("type, worker, maxJobs >= 1 and timeoutMs >= 1 are required.")));
        }

        var jobs = await activation.ActivateAsync(
            new Application.Jobs.ActivateJobsRequest(DefaultTenant, request.Type, request.Worker, TimeSpan.FromMilliseconds(request.TimeoutMs), Math.Min(request.MaxJobs, 1000), request.FetchVariables),
            request.RequestTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ActivateJobsResponseDto([.. jobs.Select(j => new JobDto(InstanceEndpoints.Id(j.Key), j.Type, InstanceEndpoints.Id(j.InstanceKey), j.ElementId, j.Retries, j.Deadline, j.Variables, j.CustomHeaders))]));
    }

    private static Task<IResult> CompleteAsync(string key, CompleteJobRequestDto request, InstanceCommandService commands, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => commands.CompleteJobAsync(DefaultTenant, new CompleteJob(k, request.Variables, request.Worker), cancellationToken));

    private static Task<IResult> FailAsync(string key, FailJobRequestDto request, InstanceCommandService commands, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => commands.FailJobAsync(
            DefaultTenant,
            new FailJob(k, request.Retries, request.ErrorMessage, request.RetryBackoffMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, request.Worker),
            cancellationToken));

    private static Task<IResult> ThrowErrorAsync(string key, ThrowJobErrorRequestDto request, InstanceCommandService commands, CancellationToken cancellationToken) =>
        InstanceEndpoints.RunAsync(key, k => commands.ThrowJobErrorAsync(DefaultTenant, new ThrowJobError(k, request.ErrorCode, request.ErrorMessage, request.Variables, request.Worker), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        string key,
        UpdateJobRequestDto request,
        InstanceCommandService commands,
        Application.Jobs.IJobQueue queue,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (InstanceEndpoints.TryKey(key) is not { } jobKey)
        {
            return InstanceEndpoints.NotFound("Job", key);
        }

        if (request.Retries is { } retries)
        {
            var result = await commands.UpdateJobRetriesAsync(DefaultTenant, new UpdateJobRetries(jobKey, retries), cancellationToken).ConfigureAwait(false);
            if (result is Failure failure)
            {
                return FailureMapper.ToProblem(failure);
            }
        }

        if (request.TimeoutMs is { } timeoutMs && !await queue.UpdateTimeoutAsync(DefaultTenant, jobKey, time.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false))
        {
            return FailureMapper.ToProblem(new Failure(new Conflict("JOB_NOT_ACTIVATED", "The job is not activated.")));
        }

        return Results.NoContent();
    }
}
