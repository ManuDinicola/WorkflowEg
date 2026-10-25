using System.Text;
using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Application.Messaging;
using Flowd.Application.Observability;
using Flowd.Application.Timers;
using Flowd.Application.UserTasks;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.Handlers;
using Flowd.Engine.Results;
using Flowd.Engine.State;
using Microsoft.Extensions.Time.Testing;

namespace Flowd.Conformance.Tests.Support;

/// <summary>
/// The conformance harness of section 22: the real Application pipeline and Interpreter over an in-memory store, driven by a
/// <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class TestEngine
{
    private const string Tenant = "";

    private readonly DeployProcessesUseCase _deployer;
    private readonly StartInstanceUseCase _starter;
    private readonly InstanceCommandService _commands;
    private readonly JobActivationService _activation;
    private readonly TimerService _timers;
    private readonly CommandProcessor _processor;
    private readonly SignalService? _signals;
    private readonly PostCommitEffects? _postCommit;
    private readonly UserTaskService _userTasks;
    private readonly MessageService _messages;

    public TestEngine(EngineOptions? options = null, InMemoryStore? store = null, FakeTimeProvider? time = null)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Store = store ?? new InMemoryStore();
        Keys = new SequentialKeys();
        var engine = new EngineServices(new Interpreter(ElementHandlerRegistry.CreateDefault(), options), Keys, Time);
        var telemetry = new EngineTelemetry(Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineTelemetry>.Instance);
        var processor = new CommandProcessor(Store, engine, Store, () => _postCommit!, telemetry);
        _processor = processor;
        _deployer = new DeployProcessesUseCase(Store, Store, Keys, Time);
        _starter = new StartInstanceUseCase(Store, Store, new ProcessModelProvider(Store), engine, processor);
        _signals = new SignalService(Store, processor, _starter);
        _messages = new MessageService(Store, processor, _starter, Keys, Time);
        _postCommit = new PostCommitEffects(_signals, _starter, processor, Store, _messages);
        _userTasks = new UserTaskService(Store, processor, new InMemoryFormStore());
        _timers = new TimerService(Store, processor, _starter, Time, telemetry);
        _commands = new InstanceCommandService(processor, Store);
        _activation = new JobActivationService(Store, Time, telemetry);
        Queue = Store;
    }

    public FakeTimeProvider Time { get; }

    public InMemoryStore Store { get; }

    public SequentialKeys Keys { get; }

    public IJobQueue Queue { get; }

    public void AdvanceTime(TimeSpan by) => Time.Advance(by);

    public async Task<ProcessDefinitionInfo> DeployAsync(string xml)
    {
        var result = await _deployer.ExecuteAsync([new DeploymentResource("test.bpmn", Encoding.UTF8.GetBytes(xml))], Tenant, CancellationToken.None);
        return Ok(result).Single().Definition;
    }

    public Task<Result<IReadOnlyList<DeployedDefinition>>> TryDeployAsync(string xml) =>
        _deployer.ExecuteAsync([new DeploymentResource("test.bpmn", Encoding.UTF8.GetBytes(xml))], Tenant, CancellationToken.None);

    public async Task<long> StartAsync(string processId, JsonObject? variables = null, int? version = null)
    {
        var result = await _starter.ExecuteAsync(new StartInstanceRequest(Tenant, processId, version, variables, null, null), CancellationToken.None);
        return Ok(result).InstanceKey;
    }

    public Task<Result<StartedInstance>> TryStartAsync(string processId, JsonObject? variables = null, string? startElementId = null) =>
        _starter.ExecuteAsync(new StartInstanceRequest(Tenant, processId, null, variables, null, startElementId), CancellationToken.None);

    public async Task<ActivatedJob?> ActivateJobAsync(string type, string worker = "worker-1", IReadOnlyList<string>? fetch = null, TimeSpan? timeout = null)
    {
        var request = new ActivateJobsRequest(Tenant, type, worker, timeout ?? TimeSpan.FromSeconds(30), 1, fetch);
        var jobs = await _activation.ActivateAsync(request, TimeSpan.Zero, CancellationToken.None);
        return jobs.Count > 0 ? jobs[0] : null;
    }

    public Task<Result<ProcessedCommand>> CompleteJobAsync(long jobKey, JsonObject? variables = null, string? worker = null) =>
        _commands.CompleteJobAsync(Tenant, new CompleteJob(jobKey, variables, worker), CancellationToken.None);

    public Task<Result<ProcessedCommand>> FailJobAsync(long jobKey, int retries, string message = "boom", TimeSpan? backoff = null, string? worker = null) =>
        _commands.FailJobAsync(Tenant, new FailJob(jobKey, retries, message, backoff, worker), CancellationToken.None);

    public Task<Result<ProcessedCommand>> ThrowErrorAsync(long jobKey, string code) =>
        _commands.ThrowJobErrorAsync(Tenant, new ThrowJobError(jobKey, code, "thrown", null, null), CancellationToken.None);

    public Task<Result<ProcessedCommand>> UpdateRetriesAsync(long jobKey, int retries) =>
        _commands.UpdateJobRetriesAsync(Tenant, new UpdateJobRetries(jobKey, retries), CancellationToken.None);

    public Task<Result<ProcessedCommand>> ModifyAsync(long instanceKey, long[] terminate, string[] activate, JsonObject? variables = null) =>
        _commands.ModifyAsync(Tenant, instanceKey, new ModifyInstance(terminate, activate, variables), CancellationToken.None);

    public Task<Result<ProcessedCommand>> CancelAsync(long instanceKey) =>
        _commands.CancelAsync(Tenant, instanceKey, CancellationToken.None);

    public Task<Result<ProcessedCommand>> SetVariablesAsync(long instanceKey, JsonObject variables, bool local = false) =>
        _commands.SetVariablesAsync(Tenant, instanceKey, new SetVariables(variables, null, local), CancellationToken.None);

    public Task<Result<ProcessedCommand>> ResolveIncidentAsync(long incidentKey) =>
        _commands.ResolveIncidentAsync(Tenant, incidentKey, CancellationToken.None);

    /// <summary>Fires every timer that is due now; returns how many were handled (FR-TMR-002).</summary>
    public Task<int> RunTimersAsync() => _timers.RunDueAsync(CancellationToken.None);

    public Task<Result<PublishedMessage>> PublishAsync(string name, string correlationKey, JsonObject? variables = null, TimeSpan? ttl = null, string? messageId = null) =>
        _messages.PublishAsync(new PublishMessageRequest(Tenant, name, correlationKey, variables, ttl ?? TimeSpan.Zero, messageId), CancellationToken.None);

    /// <summary>Runs any engine command directly, for the rejection paths the API does not expose.</summary>
    public Task<Result<ProcessedCommand>> ExecuteAsync(long instanceKey, EngineCommand command) =>
        _processor.ExecuteAsync(Tenant, instanceKey, command, CancellationToken.None);

    public Task<int> BroadcastAsync(string name, JsonObject? variables = null) =>
        _signals!.BroadcastAsync(Tenant, name, variables, CancellationToken.None);

    public Task<Result<ProcessedCommand>> ClaimTaskAsync(long taskKey, string user) => _userTasks.ClaimAsync(Tenant, taskKey, user, CancellationToken.None);

    public Task<Result<ProcessedCommand>> UnclaimTaskAsync(long taskKey) => _userTasks.UnclaimAsync(Tenant, taskKey, CancellationToken.None);

    public Task<Result<ProcessedCommand>> AssignTaskAsync(long taskKey, string user) => _userTasks.AssignAsync(Tenant, taskKey, user, CancellationToken.None);

    public Task<Result<ProcessedCommand>> CompleteTaskAsync(long taskKey, JsonObject? variables = null, string? user = null) =>
        _userTasks.CompleteAsync(Tenant, taskKey, variables, user, CancellationToken.None);

    public IReadOnlyList<UserTaskRecord> UserTasks(long instanceKey) => [.. State(instanceKey).UserTasks.Values.OrderBy(t => t.Key)];

    public Task<int> RequeueTimedOutAsync() => Queue.RequeueTimedOutAsync(Time.GetUtcNow(), CancellationToken.None);

    // ---- assertions & inspection ----------------------------------------------------------------------------------

    public EngineState State(long instanceKey) => Store.Instances[instanceKey];

    public void AssertCompleted(long instanceKey) => State(instanceKey).State.ShouldBe(InstanceState.Completed);

    public void AssertTerminated(long instanceKey) => State(instanceKey).State.ShouldBe(InstanceState.Terminated);

    public void AssertActiveElements(long instanceKey, params string[] elementIds) =>
        State(instanceKey).LiveElements().Select(e => e.ElementId).Order(StringComparer.Ordinal).ShouldBe(elementIds.Order(StringComparer.Ordinal));

    public JsonObject Variables(long instanceKey) => State(instanceKey).Variables.Visible(instanceKey);

    public IReadOnlyList<string> HistoryTypes(long instanceKey, string? elementId = null) =>
        [.. Store.HistoryOf(instanceKey).Where(h => elementId is null || h.ElementId == elementId).Select(h => h.EventType)];

    public IReadOnlyList<IncidentRecord> OpenIncidents(long instanceKey) =>
        [.. State(instanceKey).Incidents.Values.Where(i => i.State == IncidentState.Open)];

    public static T Ok<T>(Result<T> result)
    {
        if (result is Failure failure)
        {
            throw new InvalidOperationException($"Expected success but got {Describe(failure)}");
        }

        return result is T value ? value : throw new InvalidOperationException("Empty result.");
    }

    public static string Describe(Failure failure) => failure switch
    {
        ValidationFailure v => $"validation: {v.Message} {string.Join("; ", (v.Details ?? []).Select(d => $"{d.Code}@{d.Target}"))}",
        NotFound n => $"not found: {n.Message}",
        Conflict c => $"conflict {c.Code}: {c.Message}",
        PermissionDenied p => $"denied: {p.Message}",
        RateLimited r => $"rate limited: {r.Message}",
        Unavailable u => $"unavailable: {u.Message}",
    };

    public static bool Failed<T>(Result<T> result) => result is Failure;

    public static string? ConflictCode<T>(Result<T> result) =>
        result is Failure failure && failure is Conflict conflict ? conflict.Code : null;
}
