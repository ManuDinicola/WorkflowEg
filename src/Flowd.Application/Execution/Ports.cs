using Flowd.Engine.Effects;
using Flowd.Engine.State;

namespace Flowd.Application.Execution;

/// <summary>One database transaction around a single command on one instance (FR-ARCH-002).</summary>
public interface IInstanceTransaction : IAsyncDisposable
{
    /// <summary>Locks the instance row (<c>SELECT ... FOR UPDATE</c>) and loads its state. Null when the instance does not exist.</summary>
    Task<EngineState?> LockAsync(string tenantId, long instanceKey, CancellationToken cancellationToken);

    /// <summary>Persists the effects of the interpreter run, in order.</summary>
    Task ApplyAsync(EngineState state, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Port: opens command transactions.</summary>
public interface IInstanceTransactionFactory
{
    Task<IInstanceTransaction> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>Port: loads the static process model of a definition (parsed once, cached).</summary>
public interface IProcessModelProvider
{
    Task<ProcessGraph?> GetGraphAsync(string tenantId, long definitionKey, CancellationToken cancellationToken);
}

/// <summary>Port: finds which instance owns a job or an incident, so the right row can be locked (section 16).</summary>
public interface IInstanceLookup
{
    Task<long?> InstanceOfJobAsync(string tenantId, long jobKey, CancellationToken cancellationToken);

    Task<long?> InstanceOfIncidentAsync(string tenantId, long incidentKey, CancellationToken cancellationToken);
}
