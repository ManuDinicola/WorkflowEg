using System.Collections.Concurrent;
using Flowd.Application.Deployment;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Engine.State;

namespace Flowd.Application.Execution;

/// <summary>Parses a stored definition once and keeps the immutable graph in memory. Definitions never change (FR-DEP-003).</summary>
public sealed class ProcessModelProvider(IProcessDefinitionStore store) : IProcessModelProvider
{
    private readonly ConcurrentDictionary<long, ProcessGraph> _cache = new();

    public async Task<ProcessGraph?> GetGraphAsync(string tenantId, long definitionKey, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(definitionKey, out var cached))
        {
            return cached;
        }

        var info = await store.GetAsync(tenantId, definitionKey, cancellationToken).ConfigureAwait(false);
        var xml = info is null ? null : await store.GetResourceAsync(tenantId, definitionKey, cancellationToken).ConfigureAwait(false);
        if (info is null || xml is null)
        {
            return null;
        }

        using var stream = new MemoryStream(xml);
        var process = BpmnReader.Read(stream) switch
        {
            BpmnDefinitions definitions => definitions.Processes.FirstOrDefault(p => string.Equals(p.Id, info.BpmnProcessId, StringComparison.Ordinal)),
            BpmnInvalid => null,
        };
        if (process is null)
        {
            return null;
        }

        return _cache.GetOrAdd(definitionKey, new ProcessGraph(process));
    }
}
