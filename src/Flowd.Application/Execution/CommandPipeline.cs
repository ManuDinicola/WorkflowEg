using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Application.Messaging;
using Flowd.Application.Observability;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;
using Flowd.Engine.Execution;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;
using Flowd.Engine.State;

namespace Flowd.Application.Execution;

/// <summary>The pure-core collaborators every command needs: interpreter, keys and the clock (FR-ARCH-001).</summary>
public sealed record EngineServices(Interpreter Interpreter, IKeyGenerator Keys, TimeProvider Time);

/// <summary>What a processed command left behind.</summary>
public sealed record ProcessedCommand(long InstanceKey, StepOutcome Outcome);

/// <summary>Request to start an instance (API-003). Message and timer starts pin the definition and start element.</summary>
public sealed record StartInstanceRequest(
    string TenantId,
    string BpmnProcessId,
    int? Version,
    JsonObject? Variables,
    string? BusinessKey,
    string? StartElementId,
    long? DefinitionKey = null,
    long? CorrelatedMessageKey = null,
    long? ParentInstanceKey = null,
    long? ParentElementInstanceKey = null);

public sealed record StartedInstance(long InstanceKey, long DefinitionKey, int Version, string BpmnProcessId);

/// <summary>Runs one command on an existing instance in one transaction: lock, interpret, persist, commit (FR-ARCH-002).</summary>
public sealed class CommandProcessor(IInstanceTransactionFactory transactions, EngineServices engine, IMessageStore messages, Func<IPostCommitEffects> postCommit, EngineTelemetry telemetry)
{
    public async Task<Result<ProcessedCommand>> ExecuteAsync(string tenantId, long instanceKey, EngineCommand command, CancellationToken cancellationToken)
    {
        using var scope = telemetry.StartCommand(CommandName(command), instanceKey);
        await using var transaction = await transactions.BeginAsync(cancellationToken).ConfigureAwait(false);
        EngineState? state;
        var lockStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            state = await transaction.LockAsync(tenantId, instanceKey, cancellationToken).ConfigureAwait(false);
            telemetry.LockWaited(System.Diagnostics.Stopwatch.GetElapsedTime(lockStart));
        }
        catch (InstanceBusyException ex)
        {
            return new Failure(new Unavailable(ex.Message, "RETRY_LATER"));
        }

        if (state is null)
        {
            return new Failure(new NotFound($"Process instance {instanceKey} does not exist."));
        }

        var outcome = engine.Interpreter.Execute(state, command, engine.Time.GetUtcNow(), engine.Keys);
        if (outcome is CommandRejected rejected)
        {
            return rejected.Failure;
        }

        if (outcome is not CommandAccepted accepted)
        {
            throw new InvalidOperationException("Empty command outcome.");
        }

        try
        {
            await transaction.ApplyAsync(state, accepted.Effects, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CorrelationConflictException)
        {
            return new Failure(new Conflict("MESSAGE_ALREADY_CORRELATED", "The message already reached this process definition."));
        }

        await AfterCommitAsync(tenantId, accepted.Effects, cancellationToken).ConfigureAwait(false);
        return new ProcessedCommand(state.InstanceKey, accepted.Outcome);
    }

    /// <summary>The case name of a command, for traces and metrics (a union exposes its value as <c>object</c>).</summary>
    private static string CommandName(EngineCommand command) => command.Value?.GetType().Name ?? "Unknown";

    /// <summary>Everything that follows a committed command: buffered messages for new subscriptions, signals, child instances (FR-CON-002).</summary>
    public async Task AfterCommitAsync(string tenantId, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken)
    {
        telemetry.Observe(effects);
        await DeliverBufferedAsync(tenantId, effects, cancellationToken).ConfigureAwait(false);
        await postCommit().HandleAsync(tenantId, effects, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A subscription that just opened may match a message published earlier and still buffered (FR-MSG-002).
    /// Each delivery is its own command; a message reaches a definition at most once.
    /// </summary>
    public async Task DeliverBufferedAsync(string tenantId, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var subscription in effects.Select(e => e is SubscriptionChanged changed ? changed.Snapshot : null).OfType<SubscriptionRecord>())
        {
            if (subscription.InstanceKey is not { } instanceKey)
            {
                continue;
            }

            var buffered = await messages.FindBufferedAsync(tenantId, subscription.MessageName, subscription.CorrelationKey, engine.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            foreach (var message in buffered)
            {
                if (await messages.IsCorrelatedAsync(message.Key, subscription.DefinitionKey, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var command = new CorrelateMessage(subscription.Key, message.Key, message.Name, message.Variables);
                if (await ExecuteAsync(tenantId, instanceKey, command, cancellationToken).ConfigureAwait(false) is ProcessedCommand)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>Creates and starts a process instance (FR-EXEC-001, FR-DEP-003).</summary>
public sealed class StartInstanceUseCase(
    IInstanceTransactionFactory transactions,
    IProcessDefinitionStore definitions,
    IProcessModelProvider models,
    EngineServices engine,
    CommandProcessor processor)
{
    public async Task<Result<StartedInstance>> ExecuteAsync(StartInstanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = request.DefinitionKey is { } key
            ? await definitions.GetAsync(request.TenantId, key, cancellationToken).ConfigureAwait(false)
            : await definitions.FindAsync(request.TenantId, request.BpmnProcessId, request.Version, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return new Failure(new NotFound($"No deployed process '{request.BpmnProcessId}'{(request.Version is { } v ? $" version {v}" : string.Empty)}."));
        }

        var graph = await models.GetGraphAsync(request.TenantId, definition.Key, cancellationToken).ConfigureAwait(false);
        if (graph is null)
        {
            return new Failure(new Unavailable($"Definition {definition.Key} could not be loaded."));
        }

        var state = new EngineState(engine.Keys.NextKey(), definition.Key, graph, request.TenantId, engine.Time.GetUtcNow())
        {
            BusinessKey = request.BusinessKey,
            ParentInstanceKey = request.ParentInstanceKey,
            ParentElementInstanceKey = request.ParentElementInstanceKey,
        };
        var command = new StartInstance(request.Variables, request.StartElementId);
        var outcome = engine.Interpreter.Execute(state, command, state.StartedAt, engine.Keys);
        if (outcome is CommandRejected rejected)
        {
            return rejected.Failure;
        }

        if (outcome is not CommandAccepted accepted)
        {
            throw new InvalidOperationException("Empty command outcome.");
        }

        var effects = request.CorrelatedMessageKey is { } messageKey
            ? [.. accepted.Effects, new MessageCorrelated(messageKey, definition.Key)]
            : accepted.Effects;
        try
        {
            await using var transaction = await transactions.BeginAsync(cancellationToken).ConfigureAwait(false);
            await transaction.ApplyAsync(state, effects, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CorrelationConflictException)
        {
            return new Failure(new Conflict("MESSAGE_ALREADY_CORRELATED", "The message already started an instance of this definition."));
        }

        await processor.AfterCommitAsync(request.TenantId, accepted.Effects, cancellationToken).ConfigureAwait(false);

        return new StartedInstance(state.InstanceKey, definition.Key, definition.Version, definition.BpmnProcessId);
    }
}

/// <summary>Entry points for everything that targets an existing instance, a job or an incident (API-005, API-006, API-008, API-010).</summary>
public sealed class InstanceCommandService(CommandProcessor processor, IInstanceLookup lookup)
{
    public Task<Result<ProcessedCommand>> CancelAsync(string tenantId, long instanceKey, CancellationToken cancellationToken) =>
        processor.ExecuteAsync(tenantId, instanceKey, new CancelInstance(), cancellationToken);

    public Task<Result<ProcessedCommand>> SetVariablesAsync(string tenantId, long instanceKey, SetVariables command, CancellationToken cancellationToken) =>
        processor.ExecuteAsync(tenantId, instanceKey, command, cancellationToken);

    public Task<Result<ProcessedCommand>> CompleteJobAsync(string tenantId, CompleteJob command, CancellationToken cancellationToken) =>
        ForJobAsync(tenantId, command.JobKey, command, cancellationToken);

    public Task<Result<ProcessedCommand>> FailJobAsync(string tenantId, FailJob command, CancellationToken cancellationToken) =>
        ForJobAsync(tenantId, command.JobKey, command, cancellationToken);

    public Task<Result<ProcessedCommand>> ThrowJobErrorAsync(string tenantId, ThrowJobError command, CancellationToken cancellationToken) =>
        ForJobAsync(tenantId, command.JobKey, command, cancellationToken);

    public Task<Result<ProcessedCommand>> UpdateJobRetriesAsync(string tenantId, UpdateJobRetries command, CancellationToken cancellationToken) =>
        ForJobAsync(tenantId, command.JobKey, command, cancellationToken);

    public async Task<Result<ProcessedCommand>> ResolveIncidentAsync(string tenantId, long incidentKey, CancellationToken cancellationToken)
    {
        var instanceKey = await lookup.InstanceOfIncidentAsync(tenantId, incidentKey, cancellationToken).ConfigureAwait(false);
        return instanceKey is { } key
            ? await processor.ExecuteAsync(tenantId, key, new ResolveIncident(incidentKey), cancellationToken).ConfigureAwait(false)
            : new Failure(new NotFound($"Incident {incidentKey} does not exist."));
    }

    private async Task<Result<ProcessedCommand>> ForJobAsync(string tenantId, long jobKey, EngineCommand command, CancellationToken cancellationToken)
    {
        var instanceKey = await lookup.InstanceOfJobAsync(tenantId, jobKey, cancellationToken).ConfigureAwait(false);
        return instanceKey is { } key
            ? await processor.ExecuteAsync(tenantId, key, command, cancellationToken).ConfigureAwait(false)
            : new Failure(new NotFound($"Job {jobKey} does not exist."));
    }
}
