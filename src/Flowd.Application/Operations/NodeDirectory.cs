namespace Flowd.Application.Operations;

/// <summary>Port: which engine nodes are running on the database right now (FR-OPS-011).</summary>
public interface INodeDirectory
{
    /// <summary>Node ids that currently hold their node id on the database, in ascending order.</summary>
    Task<IReadOnlyList<int>> RunningNodeIdsAsync(CancellationToken cancellationToken);
}
