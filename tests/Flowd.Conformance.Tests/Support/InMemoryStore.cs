using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Application.Execution;
using Flowd.Application.Jobs;
using Flowd.Application.Messaging;
using Flowd.Application.Queries;
using Flowd.Application.Timers;
using Flowd.Application.UserTasks;
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
public sealed class InMemoryStore : IProcessDefinitionStore, IInstanceTransactionFactory, IInstanceLookup, IJobQueue, IInstanceQueries, ITimerQueue, IStartTriggerStore, IMessageStore, ISignalStore, IUserTaskQueries, IChildInstanceStore
{
    private readonly Dictionary<long, (ProcessDefinitionInfo Info, byte[] Xml, byte[] Checksum)> _definitions = [];
    private readonly Dictionary<long, EngineState> _instances = [];
    private readonly Dictionary<long, List<HistoryAppended>> _history = [];
    private readonly List<StartTimerRow> _startTimers = [];
    private readonly List<SubscriptionRecord> _startMessages = [];
    private readonly Dictionary<long, DateTimeOffset> _leases = [];
    private readonly List<BufferedMessage> _buffered = [];
    private readonly HashSet<(long Message, long Definition)> _correlations = [];

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

    public Task<DeleteDefinitionOutcome> DeleteAsync(string tenantId, long key, CancellationToken cancellationToken)
    {
        if (!_definitions.TryGetValue(key, out var d) || d.Info.TenantId != tenantId)
        {
            return Task.FromResult(DeleteDefinitionOutcome.NotFound);
        }

        if (_instances.Values.Any(i => i.DefinitionKey == key))
        {
            return Task.FromResult(DeleteDefinitionOutcome.InUse);
        }

        _definitions.Remove(key);
        return Task.FromResult(DeleteDefinitionOutcome.Deleted);
    }

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
            foreach (var correlated in effects.Select(e => e is MessageCorrelated m ? m : null).OfType<MessageCorrelated>())
            {
                if (!store._correlations.Add((correlated.MessageKey, correlated.DefinitionKey)))
                {
                    throw new CorrelationConflictException("already correlated");
                }
            }

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
            activated.Add(new ActivatedJob(job.Key, job.Type, job.InstanceKey, job.ElementId, job.Retries, job.Deadline.Value, Filter(job.Variables, request.FetchVariables), job.CustomHeaders, job.CreatedAt));
        }

        return Task.FromResult<IReadOnlyList<ActivatedJob>>(activated);
    }

    public Task<IReadOnlyList<string>> TenantsWithActivatableAsync(string type, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. _instances.Values.Where(i => i.Jobs.Values.Any(j => string.Equals(j.Type, type, StringComparison.Ordinal) && j.State == JobState.Activatable)).Select(i => i.TenantId).Distinct(StringComparer.Ordinal)]);

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

    public Task<IReadOnlyList<ElementView>?> ListLiveElementsAsync(string tenantId, long instanceKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ElementView>?>(_instances.TryGetValue(instanceKey, out var s) && s.TenantId == tenantId
            ? [.. s.LiveElements().Select(e => new ElementView(e.Key, e.ElementId, e.ElementType, e.State.ToString().ToUpperInvariant(), e.ScopeKey))]
            : null);

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

    // ---- timers and start triggers ------------------------------------------------------------------------------

    private sealed class StartTimerRow
    {
        public required long Key { get; init; }

        public required string TenantId { get; init; }

        public required string ProcessId { get; init; }

        public required long DefinitionKey { get; init; }

        public required string ElementId { get; init; }

        public DateTimeOffset? DueAt { get; set; }

        public int? Repetitions { get; set; }

        public TimeSpan? Interval { get; init; }
    }

    public Task ReplaceAsync(string tenantId, string bpmnProcessId, long definitionKey, IReadOnlyList<StartTimerDraft> timers, IReadOnlyList<StartMessageDraft> messages, CancellationToken cancellationToken)
    {
        _startTimers.RemoveAll(t => t.TenantId == tenantId && t.ProcessId == bpmnProcessId);
        _startMessages.RemoveAll(m => m.TenantId == tenantId && m.CorrelationKey == bpmnProcessId);
        _startTimers.AddRange(timers.Select(t => new StartTimerRow
        {
            Key = t.Key,
            TenantId = tenantId,
            ProcessId = bpmnProcessId,
            DefinitionKey = definitionKey,
            ElementId = t.ElementId,
            DueAt = t.DueAt,
            Repetitions = t.Repetitions,
            Interval = t.Interval,
        }));
        _startMessages.AddRange(messages.Select(m => new SubscriptionRecord
        {
            Key = m.Key,
            DefinitionKey = definitionKey,
            ElementId = m.ElementId,
            Kind = TriggerKind.Start,
            MessageName = m.MessageName,
            CorrelationKey = bpmnProcessId,
            TenantId = tenantId,
            IsSignal = m.IsSignal,
        }));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TimerLease>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan leaseFor, CancellationToken cancellationToken)
    {
        bool Free(long key) => !_leases.TryGetValue(key, out var until) || until <= now;
        var instanceTimers = _instances.Values.SelectMany(i => i.Timers.Values.Where(t => t.IsActive && t.DueAt <= now && Free(t.Key)).Select(t =>
            new TimerLease(t.Key, i.InstanceKey, i.DefinitionKey, t.ElementId, i.TenantId, t.Kind, t.DueAt, t.Repetitions, t.Interval)));
        var startTimers = _startTimers.Where(t => t.DueAt is { } due && due <= now && Free(t.Key)).Select(t =>
            new TimerLease(t.Key, null, t.DefinitionKey, t.ElementId, t.TenantId, TriggerKind.Start, t.DueAt!.Value, t.Repetitions, t.Interval));
        var leases = instanceTimers.Concat(startTimers).OrderBy(l => l.DueAt).ThenBy(l => l.Key).Take(limit).ToList();
        foreach (var lease in leases)
        {
            _leases[lease.Key] = now + leaseFor;
        }

        return Task.FromResult<IReadOnlyList<TimerLease>>(leases);
    }

    public Task AdvanceStartTimerAsync(long timerKey, DateTimeOffset? nextDue, int? repetitions, CancellationToken cancellationToken)
    {
        var row = _startTimers.First(t => t.Key == timerKey);
        row.DueAt = nextDue;
        row.Repetitions = repetitions;
        _leases.Remove(timerKey);
        return Task.CompletedTask;
    }

    public IReadOnlyList<long> StartTimerDefinitions() => [.. _startTimers.Where(t => t.DueAt is not null).Select(t => t.DefinitionKey)];

    // ---- messages -------------------------------------------------------------------------------------------------

    public Task<bool> TryBufferAsync(BufferedMessage message, CancellationToken cancellationToken)
    {
        if (message.MessageId is not null && _buffered.Any(m => m.TenantId == message.TenantId && m.Name == message.Name && m.CorrelationKey == message.CorrelationKey && m.MessageId == message.MessageId))
        {
            return Task.FromResult(false);
        }

        _buffered.Add(message);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SubscriptionRecord>> FindSubscriptionsAsync(string tenantId, string name, string correlationKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionRecord>>([.. _instances.Values
            .Where(i => i.TenantId == tenantId && i.State == InstanceState.Active)
            .SelectMany(i => i.Subscriptions.Values)
            .Where(s => !s.IsSignal && s.MessageName == name && s.CorrelationKey == correlationKey)
            .OrderBy(s => s.Key)]);

    public Task<IReadOnlyList<SubscriptionRecord>> FindStartSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionRecord>>([.. _startMessages.Where(m => m.TenantId == tenantId && !m.IsSignal && m.MessageName == name)]);

    public Task<IReadOnlyList<BufferedMessage>> FindBufferedAsync(string tenantId, string name, string correlationKey, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BufferedMessage>>([.. _buffered.Where(m => m.TenantId == tenantId && m.Name == name && m.CorrelationKey == correlationKey && m.ExpiresAt > now).OrderBy(m => m.CreatedAt).ThenBy(m => m.Key)]);

    public Task<bool> IsCorrelatedAsync(long messageKey, long definitionKey, CancellationToken cancellationToken) =>
        Task.FromResult(_correlations.Contains((messageKey, definitionKey)));

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_buffered.RemoveAll(m => m.ExpiresAt <= now));

    public int BufferedCount => _buffered.Count;

    // ---- signals and user tasks -----------------------------------------------------------------------------------

    public Task<IReadOnlyList<SubscriptionRecord>> FindSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionRecord>>([.. _instances.Values
            .Where(i => i.TenantId == tenantId && i.State == InstanceState.Active)
            .SelectMany(i => i.Subscriptions.Values)
            .Where(s => s.IsSignal && s.MessageName == name)
            .OrderBy(s => s.Key)]);

    public Task<IReadOnlyList<SubscriptionRecord>> FindStartSignalSubscriptionsAsync(string tenantId, string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionRecord>>([.. _startMessages.Where(m => m.TenantId == tenantId && m.IsSignal && m.MessageName == name)]);

    Task<UserTaskPage> IUserTaskQueries.ListAsync(string tenantId, UserTaskFilter filter, int pageSize, long? afterKey, CancellationToken cancellationToken)
    {
        var tasks = _instances.Values
            .Where(i => i.TenantId == tenantId)
            .SelectMany(i => i.UserTasks.Values)
            .Where(t => (filter.Assignee is null || t.Assignee == filter.Assignee)
                && (filter.State is null || string.Equals(t.State.ToString(), filter.State, StringComparison.OrdinalIgnoreCase))
                && (filter.CandidateGroup is null || t.CandidateGroups.Contains(filter.CandidateGroup))
                && (filter.InstanceKey is null || t.InstanceKey == filter.InstanceKey)
                && (filter.MinPriority is null || t.Priority >= filter.MinPriority))
            .OrderBy(t => t.Key)
            .Select(t => new UserTaskView(t.Key, t.InstanceKey, t.ElementInstanceKey, t.ElementId, t.Assignee, t.CandidateGroups, t.CandidateUsers, t.DueDate, t.FollowUpDate, t.Priority, t.FormKey, t.State.ToString().ToUpperInvariant(), t.CreatedAt));
        return Task.FromResult(new UserTaskPage([.. tasks.Take(pageSize)], null));
    }

    Task<UserTaskView?> IUserTaskQueries.GetAsync(string tenantId, long taskKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.Values.Where(i => i.TenantId == tenantId).SelectMany(i => i.UserTasks.Values).Where(t => t.Key == taskKey)
            .Select(t => (UserTaskView?)new UserTaskView(t.Key, t.InstanceKey, t.ElementInstanceKey, t.ElementId, t.Assignee, t.CandidateGroups, t.CandidateUsers, t.DueDate, t.FollowUpDate, t.Priority, t.FormKey, t.State.ToString().ToUpperInvariant(), t.CreatedAt)).FirstOrDefault());

    public Task<long?> InstanceOfUserTaskAsync(string tenantId, long taskKey, CancellationToken cancellationToken) =>
        Task.FromResult(_instances.Values.Where(i => i.TenantId == tenantId && i.UserTasks.ContainsKey(taskKey)).Select(i => (long?)i.InstanceKey).FirstOrDefault());

    public Task<IReadOnlyList<long>> FindActiveChildrenAsync(string tenantId, long parentElementInstanceKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<long>>([.. _instances.Values
            .Where(i => i.TenantId == tenantId && i.ParentElementInstanceKey == parentElementInstanceKey && i.State == InstanceState.Active)
            .Select(i => i.InstanceKey)]);
}
