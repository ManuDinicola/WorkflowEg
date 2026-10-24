using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowd.Application.Deployment;
using Flowd.Engine.Keys;
using Flowd.Engine.Results;

namespace Flowd.Application.Forms;

/// <summary>Parse and store form definitions (JSON files with an <c>id</c> and <c>components</c>), all-or-nothing (FR-UT-005).</summary>
public sealed class DeployFormsUseCase(IFormStore store, IKeyGenerator keys, TimeProvider time)
{
    public async Task<Result<IReadOnlyList<DeployedForm>>> ExecuteAsync(IReadOnlyList<DeploymentResource> resources, string tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            return new Failure(new ValidationFailure("A deployment needs at least one resource."));
        }

        var drafts = new List<FormDraft>();
        var details = new List<FailureDetail>();
        foreach (var resource in resources)
        {
            Collect(resource, tenantId, drafts, details);
        }

        if (details.Count > 0)
        {
            return new Failure(new ValidationFailure("The deployment contains invalid resources.", details));
        }

        IReadOnlyList<DeployedForm> deployed = await store.DeployAsync(drafts, cancellationToken).ConfigureAwait(false);
        return deployed;
    }

    private void Collect(DeploymentResource resource, string tenantId, List<FormDraft> drafts, List<FailureDetail> details)
    {
        if (resource.Content.Length > DeployProcessesUseCase.MaxResourceBytes)
        {
            details.Add(new FailureDetail(resource.Name, "RESOURCE_TOO_LARGE", "Resource is too large."));
            return;
        }

        JsonObject? schema;
        try
        {
            schema = JsonNode.Parse(resource.Content) as JsonObject;
        }
        catch (JsonException ex)
        {
            details.Add(new FailureDetail(resource.Name, "FORM_MALFORMED", ex.Message));
            return;
        }

        if (schema?["id"]?.GetValue<string>() is not { Length: > 0 } id || schema["components"] is not JsonArray)
        {
            details.Add(new FailureDetail(resource.Name, "FORM_INVALID", "A form needs an \"id\" and a \"components\" list."));
            return;
        }

        drafts.Add(new FormDraft(keys.NextKey(), tenantId, id, resource.Name, schema, SHA256.HashData(resource.Content), time.GetUtcNow()));
    }
}
