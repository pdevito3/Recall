namespace Recall.Server.Domain.ModelProviders.Features;

using Databases;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Resources.Entrypoints.Http;

/// <summary>
/// A person who owns a journal sees where a Cleanup sends the words, with no permission to manage the providers. It uses the
/// rule of <c>CleanupJob</c>: the newest activation is the active one, and with no model it takes no words.
/// </summary>
public static class GetActiveModelProvider
{
    public sealed record Query : IRequest<ActiveModelProviderDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, ActiveModelProviderDto>
    {
        public async Task<ActiveModelProviderDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();

            var active = await dbContext.ModelProviders.AsNoTracking()
                .TagWithOperationCallSite("model_providers.active")
                .NewestActivationFirst()
                .Select(p => new { p.Name, p.Model })
                .FirstOrDefaultAsync(cancellationToken);

            return new ActiveModelProviderDto { Name = active?.Model is null ? null : active.Name };
        }
    }
}

public sealed class GetActiveModelProviderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/model-providers/active", Handle)
        .WithName("GetActiveModelProvider")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the name of the model provider that gets the words of the next Cleanup of the caller.
    /// </summary>
    public static async Task<Ok<ActiveModelProviderDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetActiveModelProvider.Query(), cancellationToken));
}
