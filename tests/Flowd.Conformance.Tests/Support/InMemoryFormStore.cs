using Flowd.Application.Forms;

namespace Flowd.Conformance.Tests.Support;

/// <summary>Keeps forms in memory for the conformance tests.</summary>
internal sealed class InMemoryFormStore : IFormStore
{
    private readonly List<(FormInfo Info, FormDraft Draft)> _forms = [];

    public Task<IReadOnlyList<DeployedForm>> DeployAsync(IReadOnlyList<FormDraft> drafts, CancellationToken cancellationToken)
    {
        var result = new List<DeployedForm>();
        foreach (var draft in drafts)
        {
            var version = _forms.Count(f => f.Info.FormId == draft.FormId && f.Info.TenantId == draft.TenantId) + 1;
            var info = new FormInfo(draft.Key, draft.TenantId, draft.FormId, version, draft.ResourceName, draft.DeployedAt);
            _forms.Add((info, draft));
            result.Add(new DeployedForm(info, IsNewVersion: true));
        }

        return Task.FromResult<IReadOnlyList<DeployedForm>>(result);
    }

    public Task<FormDocument?> FindAsync(string tenantId, string formId, CancellationToken cancellationToken)
    {
        var latest = _forms.Where(f => f.Info.TenantId == tenantId && f.Info.FormId == formId).OrderByDescending(f => f.Info.Version).FirstOrDefault();
        return Task.FromResult(latest.Info is null ? null : new FormDocument(latest.Info, latest.Draft.Schema));
    }

    public Task<IReadOnlyList<FormInfo>> ListAsync(string tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FormInfo>>([.. _forms.Where(f => f.Info.TenantId == tenantId).Select(f => f.Info)]);
}
