using System.Globalization;
using System.Text.Json.Nodes;
using Flowd.Api.Security;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Application.Messaging;
using Flowd.Engine.Commands;
using Flowd.Engine.Results;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Flowd.Api.Grpc;

/// <summary>
/// The gRPC API (FR-API-020): the same use cases and the same authorization policies as the REST endpoints, for workers and clients that prefer gRPC.
/// Failures become gRPC status codes (not found, failed precondition, permission denied, invalid argument, resource exhausted, unavailable).
/// </summary>
public sealed class GatewayService(StartInstanceUseCase start, JobActivationService activation, InstanceCommandService commands, MessageService messages) : Gateway.GatewayBase
{
    [Authorize(Policy = Policies.Start)]
    public override async Task<StartInstanceReply> StartInstance(StartInstanceRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var user = context.GetHttpContext().User;
        if (!user.MayUseProcess(request.BpmnProcessId))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"This API key may not use process '{request.BpmnProcessId}'."));
        }

        var result = await start.ExecuteAsync(
            new Application.Execution.StartInstanceRequest(user.TenantId(), request.BpmnProcessId, request.Version > 0 ? request.Version : null, Variables(request.VariablesJson), request.BusinessKey.Length > 0 ? request.BusinessKey : null, null),
            context.CancellationToken).ConfigureAwait(false);
        return result switch
        {
            StartedInstance started => new StartInstanceReply { ProcessInstanceKey = Id(started.InstanceKey), Version = started.Version },
            Failure failure => throw ToRpc(failure),
        };
    }

    [Authorize(Policy = Policies.Work)]
    public override async Task<ActivateJobsReply> ActivateJobs(ActivateJobsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.Type.Length == 0 || request.Worker.Length == 0 || request.MaxJobs < 1 || request.TimeoutMs < 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "type, worker, max_jobs >= 1 and timeout_ms >= 1 are required."));
        }

        var jobs = await activation.ActivateAsync(
            new Application.Jobs.ActivateJobsRequest(context.GetHttpContext().User.TenantId(), request.Type, request.Worker, TimeSpan.FromMilliseconds(request.TimeoutMs), Math.Min(request.MaxJobs, 1000), null),
            request.RequestTimeoutMs > 0 ? TimeSpan.FromMilliseconds(request.RequestTimeoutMs) : null,
            context.CancellationToken).ConfigureAwait(false);
        var reply = new ActivateJobsReply();
        foreach (var job in jobs)
        {
            var item = new Job
            {
                Key = Id(job.Key),
                Type = job.Type,
                ProcessInstanceKey = Id(job.InstanceKey),
                ElementId = job.ElementId,
                Retries = job.Retries,
                VariablesJson = job.Variables.ToJsonString(),
            };
            foreach (var (name, value) in job.CustomHeaders)
            {
                item.CustomHeaders[name] = value;
            }

            reply.Jobs.Add(item);
        }

        return reply;
    }

    [Authorize(Policy = Policies.Work)]
    public override async Task<Empty> CompleteJob(CompleteJobRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.GetHttpContext().User.TenantId();
        return Done(await commands.CompleteJobAsync(tenant, new CompleteJob(ParseKey(request.JobKey), Variables(request.VariablesJson), Worker(request.Worker)), context.CancellationToken).ConfigureAwait(false));
    }

    [Authorize(Policy = Policies.Work)]
    public override async Task<Empty> FailJob(FailJobRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.GetHttpContext().User.TenantId();
        TimeSpan? backoff = request.RetryBackoffMs > 0 ? TimeSpan.FromMilliseconds(request.RetryBackoffMs) : null;
        return Done(await commands.FailJobAsync(tenant, new FailJob(ParseKey(request.JobKey), request.Retries, request.ErrorMessage, backoff, Worker(request.Worker)), context.CancellationToken).ConfigureAwait(false));
    }

    [Authorize(Policy = Policies.Publish)]
    public override async Task<PublishMessageReply> PublishMessage(PublishMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var result = await messages.PublishAsync(
            new Application.Messaging.PublishMessageRequest(context.GetHttpContext().User.TenantId(), request.Name, request.CorrelationKey, Variables(request.VariablesJson), TimeSpan.FromMilliseconds(request.TtlMs), request.MessageId.Length > 0 ? request.MessageId : null),
            context.CancellationToken).ConfigureAwait(false);
        return result switch
        {
            PublishedMessage published => new PublishMessageReply { MessageKey = Id(published.Key) },
            Failure failure => throw ToRpc(failure),
        };
    }

    private static Empty Done(Result<ProcessedCommand> result) => result switch
    {
        ProcessedCommand => new Empty(),
        Failure failure => throw ToRpc(failure),
    };

    private static string? Worker(string worker) => worker.Length > 0 ? worker : null;

    private static string Id(long key) => key.ToString(CultureInfo.InvariantCulture);

    private static long ParseKey(string key) =>
        long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new RpcException(new Status(StatusCode.NotFound, $"Resource '{key}' not found."));

    private static JsonObject? Variables(string json)
    {
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "variables_json must be a JSON object."));
        }
        catch (System.Text.Json.JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "variables_json is not valid JSON."));
        }
    }

    private static RpcException ToRpc(Failure failure) => failure switch
    {
        ValidationFailure v => new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFound n => new RpcException(new Status(StatusCode.NotFound, n.Message)),
        Conflict c => new RpcException(new Status(StatusCode.FailedPrecondition, $"{c.Code}: {c.Message}")),
        PermissionDenied p => new RpcException(new Status(StatusCode.PermissionDenied, p.Message)),
        RateLimited r => new RpcException(new Status(StatusCode.ResourceExhausted, r.Message)),
        Unavailable u => new RpcException(new Status(StatusCode.Unavailable, u.Message)),
    };
}
