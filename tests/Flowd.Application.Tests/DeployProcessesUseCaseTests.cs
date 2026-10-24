using System.Text;
using Flowd.Application.Deployment;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;

namespace Flowd.Application.Tests;

/// <summary>FR-DEP-001, FR-DEP-004 and FR-SEC-004 at the use-case level (store faked; Postgres is covered by integration tests).</summary>
public sealed class DeployProcessesUseCaseTests
{
    private const string ValidXml = """
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
          <process id="p" isExecutable="true"><startEvent id="S"/></process>
        </definitions>
        """;

    private readonly FakeStore _store = new();
    private readonly DeployProcessesUseCase _useCase;

    public DeployProcessesUseCaseTests() => _useCase = new DeployProcessesUseCase(_store, new CountingKeys(), TimeProvider.System);

    private static DeploymentResource Resource(string name, string xml) => new(name, Encoding.UTF8.GetBytes(xml));

    private static Failure FailureOf(Result<IReadOnlyList<DeployedDefinition>> result) => result switch
    {
        Failure f => f,
        IReadOnlyList<DeployedDefinition> => throw new InvalidOperationException("expected a failure"),
    };

    [Fact]
    public async Task FR_DEP_001_valid_resource_is_stored_with_checksum_and_key()
    {
        var result = await _useCase.ExecuteAsync([Resource("p.bpmn", ValidXml)], "", CancellationToken.None);

        (result switch { IReadOnlyList<DeployedDefinition> d => d.Count, Failure => -1 }).ShouldBe(1);
        var draft = _store.Drafts.ShouldHaveSingleItem();
        draft.BpmnProcessId.ShouldBe("p");
        draft.Checksum.Length.ShouldBe(32);
        draft.Key.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FR_DEP_001_one_invalid_resource_rejects_the_whole_deployment()
    {
        var result = await _useCase.ExecuteAsync([Resource("ok.bpmn", ValidXml), Resource("bad.bpmn", "<oops")], "", CancellationToken.None);

        var failure = FailureOf(result);
        var validation = failure switch
        {
            ValidationFailure v => v,
            NotFound or Conflict or PermissionDenied or RateLimited or Unavailable => throw new InvalidOperationException("wrong case"),
        };
        validation.Details.ShouldNotBeNull().ShouldContain(d => d.Target == "bad.bpmn" && d.Code == "MALFORMED_XML");
        _store.Drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_DEP_001_empty_deployment_is_rejected()
    {
        var result = await _useCase.ExecuteAsync([], "", CancellationToken.None);

        _ = FailureOf(result);
        _store.Drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task FR_SEC_004_resource_over_2_mib_is_rejected()
    {
        var big = new DeploymentResource("big.bpmn", new byte[DeployProcessesUseCase.MaxResourceBytes + 1]);

        var result = await _useCase.ExecuteAsync([big], "", CancellationToken.None);

        _ = FailureOf(result);
        _store.Drafts.ShouldBeEmpty();
    }

    private sealed class CountingKeys : IKeyGenerator
    {
        private long _next;

        public long NextKey() => ++_next;
    }

    private sealed class FakeStore : IProcessDefinitionStore
    {
        public List<DefinitionDraft> Drafts { get; } = [];

        public Task<IReadOnlyList<DeployedDefinition>> DeployAsync(IReadOnlyList<DefinitionDraft> drafts, CancellationToken cancellationToken)
        {
            Drafts.AddRange(drafts);
            IReadOnlyList<DeployedDefinition> deployed =
            [
                .. drafts.Select(d => new DeployedDefinition(
                    new ProcessDefinitionInfo(d.Key, d.TenantId, d.BpmnProcessId, 1, d.ResourceName, d.DeployedAt),
                    IsNewVersion: true)),
            ];
            return Task.FromResult(deployed);
        }

        public Task<ProcessDefinitionInfo?> GetAsync(string tenantId, long key, CancellationToken cancellationToken) =>
            Task.FromResult<ProcessDefinitionInfo?>(null);

        public Task<byte[]?> GetResourceAsync(string tenantId, long key, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(null);

        public Task<DefinitionPage> ListAsync(string tenantId, int pageSize, long? afterKey, CancellationToken cancellationToken) =>
            Task.FromResult(new DefinitionPage([], null));
    }
}
