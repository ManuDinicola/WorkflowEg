using Flowd.Application.Jobs;

namespace Flowd.Application.Connectors;

/// <summary>A built-in connector: runs the jobs of one internal task type (HTTP, e-mail, message queue, ...). New connectors only add a class and a registration.</summary>
public interface IConnectorHandler
{
    string TaskType { get; }

    Task<ConnectorResult> ExecuteAsync(ActivatedJob job, CancellationToken cancellationToken);
}
