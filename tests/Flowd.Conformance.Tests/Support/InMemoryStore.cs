using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Application.Queries;
using Flowd.Engine.Effects;
using Flowd.Engine.Keys;
using Flowd.Engine.State;

namespace Flowd.Conformance.Tests.Support;

/// <summary>Counting key generator: deterministic keys for conformance and determinism tests.</summary>
public sealed class SequentialKeys : IKeyGenerator
{
    private long _next = 1000;

    public long NextKey() => ++_next;
}

/// <summary>
/// In-memory implementation of every storage port, so the real Application pipeline and Interpreter run without a database
/// (section 22: "in-memory store + FakeTimeProvider").
/// </summary>
public sealed class InMemoryStore : IProcessDefinitionStore, IInstanceTransactionFactory, IInstanceLookup, IJobQueue, IInstanceQueries
{
    private readonly Dictionary<long, (ProcessDefinitionInfo Info, byte[] Xml, byte[] Checksum)> _definitions = [];
    private readonly Dictionary<long, EngineState> _instances = [];
    private readonly Dictionary<long, List<HistoryAppended>> _history = [];

    public IReadOnlyDictionary<long, EngineState> Instances => _instances;

    public IReadOnlyList<HistoryAppended> HistoryOf(long instanceKey) =>
        _history.TryGetValue(instanceKey, out var list) ? list : [];

    // ---- definitions ----------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<DeployedDefinition>> DeployAsync(IReadOnlyList<DefinitionDraft> drafts, CancellationToken cancellationToken)
    {
        var result = new List<DeployedDefinition>();
        foreach (var draft in drafts)
        {
            var latest = _definitions.Values
                .Where(d => d.Info.TenantId == draft.TenantId && d.Info.BpmnProcessId == draft.BpmnProcessId)
                .OrderByDescending(d => d.Info.Version)
                .Cast<(ProcessDefinitionInfo Info, byte[] Xml, byte[] Checksum)?>()
                .FirstOrDefault();
            if (latest is { } existing && existing.Checksum.AsSpan().SequenceEqual(draft.Checksum))
            {
                result.Add(new DeployedDefinition(existing.Info, IsNewVersion: false));
                continue;
            }

            var info = new ProcessDefinitionInfo(draft.Key, draft.TenantId, draft.BpmnProcessId, (latest?.Info.Version ?? 0) + 1, draft.ResourceName, draft.DeployedAt);
            _definitions[draft.Key] = (info, draft.Resource, draft.Checksum);
            result.Add(new DeployedDefinition(info, IsNewVersion: true));
        }

        return Task.FromResult<IReadOnlyList<DeployedDefinition>>(result);
    }

    public Task<ProcessDefinitionInfo?> GetAsync(string tenantId, long key, CancellationToken cancellationToken) =>
        Task.FromResult(_definitions.TryGetValue(key, out var d) && d.Info.TenantId == tenantId ? d.Info : null);

    public Task<ProcessDefinitionInfo?> FindAsync(string tenantId, string bpmnProcessId, int? version, CancellationToken cancellationToken) =>
        Task.FromResult(_definitions.Values
            .Where(d => d.Info.TenantId == tenantId && d.Info.BpmnProcessId == bpmnProcessId && (version is null || d.Info.Version == version))
            .OrderByDescending(d => d.Info.Version)
            .Select(d => d.Info)
            .FirstOrDefault());

    public Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken) =>
        Task.FromResult(_definitions.TryGetValue(key, out var d) && d.Info.TenantId == tenantId ? d.Xml : null);

    public Task<DefinitionPage> ListAsync(string tenantId, int pageSize, long? afterKey, CancellationToken cancellationToken) =>
        Task.FromResult(new DefinitionPage([.. _definitions.Values.Select(d => d.Info).Where(i => i.TenantId == tenantId).OrderBy(i => i.Key).Take(pageSize)], null));

    public static byte[] Checksum(byte[] xml) => SHA256.HashData(xml);

    // ---- transactions ---------------------------------------------------------------------------------------------

    public Task<IInstanceTransaction> BeginAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IInstanceTransaction>(new Transaction(this));

    private sealed class Transaction(InMemoryStore store) : IInstanceTransaction
    {
        private readonly List<HistoryAppended> _pendingHistory = [];
        private EngineState? _working;

        public Task<EngineState?> LockAsync(string tenantId, long instanceKey, CancellationToken cancellationToken)
        {
            _working = store._instances.TryGetValue(instanceKey, out var state) && state.TenantId == tenantId ? state.Clone() : null;
            return Task.FromResult(_working);
        }

        public Task ApplyAsync(EngineState state, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken)
        {
            _working = state;
            _pendingHistory.AddRange(effects.Select(e => e is HistoryAppended h ? h : null).OfType<HistoryAppended>());
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_working is { } state)
            {
                store._instances[state.InstanceKey] = state.Clone();
                if (!store._history.TryGetValue(state.InstanceKey, out var list))
                {
                    store._history[state.InstanceKey] = list = [];
                }

                list.AddRange(_pendingHistory);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- lookups --------------------------------------------------------------------------------------------------

    public Task<long?> InstanceOfJobAsync(string tenantId, long jobKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.Values.Where(i => i.TenantId == tenantId && i.Jobs.ContainsKey(jobKey)).Select(i => (long?)i.InstanceKey).FirstOrDefault());

    public Task<long?> InstanceOfIncidentAsync(string tenantId, long incidentKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.Values.Where(i => i.TenantId == tenantId && i.Incidents.ContainsKey(incidentKey)).Select(i => (long?)i.InstanceKey).FirstOrDefault());

    // ---- job queue ------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<ActivatedJob>> ActivateAsync(ActivateJobsRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobs = _instances.Values
            .Where(i => i.TenantId == request.TenantId)
            .SelectMany(i => i.Jobs.Values)
            .Where(j => string.Equals(j.Type, request.Type, StringComparison.Ordinal) && j.State == JobState.Activatable && (j.RetryBackoffUntil is null || j.RetryBackoffUntil <= now))
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Key)
            .Take(request.MaxJobs)
            .ToList();
        var activated = new List<ActivatedJob>();
        foreach (var job in jobs)
        {
            job.State = JobState.Activated;
            job.Worker = request.Worker;
            job.Deadline = now + request.Timeout;
            activated.Add(new ActivatedJob(job.Key, job.Type, job.InstanceKey, job.ElementId, job.Retries, job.Deadline.Value, Filter(job.Variables, request.FetchVariables), job.CustomHeaders));
        }

        return Task.FromResult<IReadOnlyList<ActivatedJob>>(activated);
    }

    public Task<int> RequeueTimedOutAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var timedOut = _instances.Values.SelectMany(i => i.Jobs.Values).Where(j => j.State == JobState.Activated && j.Deadline < now).ToList();
        foreach (var job in timedOut)
        {
            job.State = JobState.Activatable;
            job.Worker = null;
            job.Deadline = null;
        }

        return Task.FromResult(timedOut.Count);
    }

    public Task<bool> UpdateTimeoutAsync(string tenantId, long jobKey, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var job = _instances.Values.Where(i => i.TenantId == tenantId).SelectMany(i => i.Jobs.Values).FirstOrDefault(j => j.Key == jobKey && j.State == JobState.Activated);
        if (job is not null)
        {
            job.Deadline = deadline;
        }

        return Task.FromResult(job is not null);
    }

    private static JsonObject Filter(JsonObject variables, IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return (JsonObject)variables.DeepClone();
        }

        var result = new JsonObject();
        foreach (var name in names.Where(variables.ContainsKey))
        {
            result[name] = variables[name]?.DeepClone();
        }

        return result;
    }

    // ---- queries --------------------------------------------------------------------------------------------------

    public Task<InstanceView?> GetInstanceAsync(string tenantId, long instanceKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.TryGetValue(instanceKey, out var s) && s.TenantId == tenantId ? ToView(s) : null);

    public Task<InstancePage> ListInstancesAsync(string tenantId, InstanceFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken) =>
        Task.FromResult(new InstancePage([.. _instances.Values.Where(s => s.TenantId == tenantId).OrderBy(s => s.InstanceKey).Take(pageSize).Select(ToView)], null));

    public Task<JsonObject?> GetVariablesAsync(string tenantId, long instanceKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.TryGetValue(instanceKey, out var s) && s.TenantId == tenantId ? s.Variables.Visible(s.InstanceKey) : null);

    public Task<HistoryPage> GetHistoryAsync(string tenantId, long instanceKey, int pageSize, long? afterSeq, CancellationToken cancellationToken)
    {
        var entries = HistoryOf(instanceKey).Select((h, i) => new HistoryEntry(i + 1, instanceKey, h.ElementInstanceKey, h.EventType, h.ElementId, h.Payload ?? [], h.OccurredAt));
        return Task.FromResult(new HistoryPage([.. entries.Where(e => e.Seq > (afterSeq ?? 0)).Take(pageSize)], null));
    }

    public Task<IncidentPage> ListIncidentsAsync(string tenantId, long? instanceKey, string? state, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        var incidents = _instances.Values
            .Where(s => s.TenantId == tenantId && (instanceKey is null || s.InstanceKey == instanceKey))
            .SelectMany(s => s.Incidents.Values)
            .Where(i => state is null || string.Equals(i.State.ToString(), state, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Key)
            .Select(i => new IncidentView(i.Key, i.InstanceKey, i.ElementInstanceKey, i.JobKey, i.ErrorType, i.ErrorMessage, i.State.ToString().ToUpperInvariant(), i.CreatedAt, i.ResolvedAt));
        return Task.FromResult(new IncidentPage([.. incidents.Take(pageSize)], null));
    }

    private InstanceView ToView(EngineState s)
    {
        var info = _definitions[s.DefinitionKey].Info;
        return new InstanceView(s.InstanceKey, s.DefinitionKey, info.BpmnProcessId, info.Version, s.State.ToString().ToUpperInvariant(), s.StartedAt, s.EndedAt, s.BusinessKey);
    }
}
