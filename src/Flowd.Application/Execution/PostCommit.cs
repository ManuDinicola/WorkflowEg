using Flowd.Application.Messaging;
using Flowd.Engine.Commands;
using Flowd.Engine.Effects;

namespace Flowd.Application.Execution;

/// <summary>Port: work that must happen after a command committed, in transactions of its own (FR-CON-002).</summary>
public interface IPostCommitEffects
{
    Task HandleAsync(string tenantId, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken);
}

/// <summary>Port: finds the running child instances of a call activity (FR-EXEC-015).</summary>
public interface IChildInstanceStore
{
    Task<IReadOnlyList<long>> FindActiveChildrenAsync(string tenantId, long parentElementInstanceKey, CancellationToken cancellationToken);
}

/// <summary>
/// Performs the cross-instance consequences of a command: broadcasts thrown signals, starts child instances for call activities,
/// reports finished children to their parents and cancels children of ended call activities.
/// </summary>
/// <summary>Port: publishes a message thrown by a process (implemented by the message service, FR-MSG-006).</summary>
public interface IMessagePublisher
{
    Task PublishThrownAsync(string tenantId, string name, string correlationKey, System.Text.Json.Nodes.JsonObject variables, CancellationToken cancellationToken);
}

public sealed class PostCommitEffects(ISignalBroadcaster signals, StartInstanceUseCase starter, CommandProcessor processor, IChildInstanceStore children, IMessagePublisher messages) : IPostCommitEffects
{
    public async Task HandleAsync(string tenantId, IReadOnlyList<EngineEffect> effects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case SignalThrown thrown:
                    await signals.BroadcastAsync(tenantId, thrown.Name, thrown.Variables, cancellationToken).ConfigureAwait(false);
                    break;
                case MessageThrown thrown:
                    await messages.PublishThrownAsync(tenantId, thrown.Name, thrown.CorrelationKey, thrown.Variables, cancellationToken).ConfigureAwait(false);
                    break;
                case ChildInstanceRequested requested:
                    await StartChildAsync(tenantId, requested, cancellationToken).ConfigureAwait(false);
                    break;
                case ChildInstanceEnded ended:
                    await ReportEndAsync(tenantId, ended, cancellationToken).ConfigureAwait(false);
                    break;
                case ChildErrorThrown error:
                    await processor.ExecuteAsync(tenantId, error.ParentInstanceKey, new ThrowFromCallActivity(error.ParentElementInstanceKey, error.ErrorCode), cancellationToken).ConfigureAwait(false);
                    break;
                case ChildCancelRequested cancel:
                    await CancelChildrenAsync(tenantId, cancel, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task StartChildAsync(string tenantId, ChildInstanceRequested requested, CancellationToken cancellationToken)
    {
        var request = new StartInstanceRequest(tenantId, requested.CalledProcessId, null, requested.Variables, null, null, null, null, requested.ParentInstanceKey, requested.ParentElementInstanceKey, requested.VersionTag);
        var result = await starter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is Engine.Results.Failure failure)
        {
            var message = failure is Engine.Results.NotFound notFound ? notFound.Message : $"The child instance of '{requested.CalledProcessId}' could not be started.";
            await processor.ExecuteAsync(tenantId, requested.ParentInstanceKey, new FailCallActivity(requested.ParentElementInstanceKey, message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportEndAsync(string tenantId, ChildInstanceEnded ended, CancellationToken cancellationToken)
    {
        EngineCommand command = ended.Completed
            ? new CompleteCallActivity(ended.ParentElementInstanceKey, ended.Variables)
            : new FailCallActivity(ended.ParentElementInstanceKey, "The child instance was terminated.");
        await processor.ExecuteAsync(tenantId, ended.ParentInstanceKey, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelChildrenAsync(string tenantId, ChildCancelRequested cancel, CancellationToken cancellationToken)
    {
        foreach (var child in await children.FindActiveChildrenAsync(tenantId, cancel.ParentElementInstanceKey, cancellationToken).ConfigureAwait(false))
        {
            await processor.ExecuteAsync(tenantId, child, new CancelInstance(), cancellationToken).ConfigureAwait(false);
        }
    }
}
