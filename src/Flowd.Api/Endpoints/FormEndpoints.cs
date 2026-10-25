using System.Globalization;
using System.Security.Claims;
using Flowd.Api.Errors;
using Flowd.Api.Security;
using Flowd.Application.Deployment;
using Flowd.Application.Forms;
using Flowd.Application.UserTasks;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>User task forms: deploy form files, read them, and read the form of a task (FR-UT-005).</summary>
public static class FormEndpoints
{
    public static IEndpointRouteBuilder MapFormEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/v1");
        group.MapPost("/forms", DeployAsync).DisableAntiforgery().RequireAuthorization(Policies.Deploy);
        group.MapGet("/forms", ListAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/forms/{formId}", GetAsync).RequireAuthorization(Policies.Read);
        group.MapGet("/user-tasks/{key}/form", TaskFormAsync).RequireAuthorization(Policies.UserTask);
        return routes;
    }

    private static async Task<IResult> DeployAsync(ClaimsPrincipal user, HttpRequest request, DeployFormsUseCase useCase, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return FailureMapper.ToProblem(new Failure(new ValidationFailure("Send the form files as multipart/form-data.")));
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resources = new List<DeploymentResource>();
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            resources.Add(new DeploymentResource(file.FileName, buffer.ToArray()));
        }

        return await useCase.ExecuteAsync(resources, user.TenantId(), cancellationToken).ConfigureAwait(false) switch
        {
            IReadOnlyList<DeployedForm> deployed => Results.Ok(new { forms = deployed.Select(d => new { key = d.Form.Key.ToString(CultureInfo.InvariantCulture), formId = d.Form.FormId, version = d.Form.Version, isNewVersion = d.IsNewVersion }) }),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IFormStore store, CancellationToken cancellationToken) =>
        Results.Ok(new { items = (await store.ListAsync(user.TenantId(), cancellationToken).ConfigureAwait(false)).Select(f => new { formId = f.FormId, version = f.Version, resourceName = f.ResourceName }) });

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, string formId, IFormStore store, CancellationToken cancellationToken) =>
        await store.FindAsync(user.TenantId(), formId, cancellationToken).ConfigureAwait(false) is { } form
            ? Results.Ok(new { formId = form.Info.FormId, version = form.Info.Version, schema = form.Schema })
            : FailureMapper.ToProblem(new Failure(new NotFound($"Form '{formId}' not found.")));

    private static async Task<IResult> TaskFormAsync(ClaimsPrincipal user, string key, IUserTaskQueries tasks, IFormStore store, CancellationToken cancellationToken)
    {
        var task = InstanceEndpoints.TryKey(key) is { } taskKey ? await tasks.GetAsync(user.TenantId(), taskKey, cancellationToken).ConfigureAwait(false) : null;
        if (task is null || !user.MayWorkOn(task.Assignee, task.CandidateGroups, task.CandidateUsers))
        {
            return FailureMapper.ToProblem(new Failure(new NotFound("User task not found.")));
        }

        return task.FormKey is { Length: > 0 } formKey && await store.FindAsync(user.TenantId(), formKey, cancellationToken).ConfigureAwait(false) is { } form
            ? Results.Ok(new { formId = form.Info.FormId, version = form.Info.Version, schema = form.Schema })
            : FailureMapper.ToProblem(new Failure(new NotFound("The task has no deployed form.")));
    }
}
