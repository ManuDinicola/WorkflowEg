using System.Text.Json.Nodes;

namespace Flowd.Application.Forms;

/// <summary>A form ready to be stored; the store assigns the version.</summary>
public sealed record FormDraft(long Key, string TenantId, string FormId, string ResourceName, JsonObject Schema, byte[] Checksum, DateTimeOffset DeployedAt);

/// <summary>Stored form metadata.</summary>
public sealed record FormInfo(long Key, string TenantId, string FormId, int Version, string ResourceName, DateTimeOffset DeployedAt);

/// <summary>Outcome for one form of a deployment: new version, or the existing one when the content matched.</summary>
public sealed record DeployedForm(FormInfo Form, bool IsNewVersion);

/// <summary>A form with its schema.</summary>
public sealed record FormDocument(FormInfo Info, JsonObject Schema);

/// <summary>Port: persistence of user task forms (FR-UT-005).</summary>
public interface IFormStore
{
    Task<IReadOnlyList<DeployedForm>> DeployAsync(IReadOnlyList<FormDraft> drafts, CancellationToken cancellationToken);

    /// <summary>The latest version of a form, or null.</summary>
    Task<FormDocument?> FindAsync(string tenantId, string formId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FormInfo>> ListAsync(string tenantId, CancellationToken cancellationToken);
}
