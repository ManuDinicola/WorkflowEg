using System.Security.Claims;
using Flowd.Api.Security;
using System.Globalization;
using Flowd.Api.Errors;
using Flowd.Application.Messaging;
using Flowd.Contracts.Execution;
using Flowd.Engine.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Publish a message (API-009, FR-MSG-001).</summary>
public static class MessageEndpoints
{

    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/messages", PublishAsync).RequireAuthorization(Policies.Publish);
        return routes;
    }

    private static async Task<IResult> PublishAsync(ClaimsPrincipal user, PublishMessageRequestDto request, MessageService messages, CancellationToken cancellationToken)
    {
        var result = await messages.PublishAsync(
            new PublishMessageRequest(user.TenantId(), request.Name, request.CorrelationKey, request.Variables, TimeSpan.FromMilliseconds(request.TtlMs ?? 0), request.MessageId),
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            PublishedMessage published => Results.Ok(new PublishMessageResponseDto(published.Key.ToString(CultureInfo.InvariantCulture), published.Correlations)),
            Failure failure => FailureMapper.ToProblem(failure),
        };
    }
}
