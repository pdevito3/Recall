namespace Recall.Server.Domain.ModelProviders.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Recordings;
using Resources;
using Resources.Entrypoints.Http;
using Serilog;
using Users;

/// <summary>
/// A person who owns a journal sees where the audio becomes text and why, with no permission to manage the providers. It uses
/// the rule that a finished Recording uses: <see cref="ModelProviderQueries.TranscriptionFirst"/>.
/// </summary>
public static class GetTranscriptionEngine
{
    public sealed record Query : IRequest<TranscriptionEngineDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, TranscriptionEngineDto>
    {
        public async Task<TranscriptionEngineDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            return await TranscriptionEngineOf(dbContext, cancellationToken);
        }
    }

    public static async Task<TranscriptionEngineDto> TranscriptionEngineOf(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var chosen = await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("model_providers.transcription.chosen")
            .TranscriptionFirst()
            .Select(p => new { p.Id, p.Name, p.TranscriptionModel })
            .FirstOrDefaultAsync(cancellationToken);
        if (chosen is not null)
        {
            return new TranscriptionEngineDto
            {
                Engine = nameof(TranscriptionEngineKind.Cloud),
                Reason = nameof(TranscriptionEngineReason.ProviderChosen),
                Text = $"The audio of each new Recording goes to {chosen.Name}, and {chosen.Name} makes the text. Audio that waits from before keeps its engine.",
                ModelProviderId = chosen.Id,
                ModelProviderName = chosen.Name,
                Model = chosen.TranscriptionModel,
            };
        }

        var anyTakesAudio = await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("model_providers.transcription.takes_audio")
            .AnyAsync(p => p.Kind == ModelProviderKind.OpenAiCompatible, cancellationToken);
        return anyTakesAudio
            ? new TranscriptionEngineDto
            {
                Engine = nameof(TranscriptionEngineKind.Local),
                Reason = nameof(TranscriptionEngineReason.NoProviderChosen),
                Text = "The server makes the text, and the audio stays on the server. An Admin can send the audio to a connected OpenAI-compatible provider instead.",
            }
            : new TranscriptionEngineDto
            {
                Engine = nameof(TranscriptionEngineKind.Local),
                Reason = nameof(TranscriptionEngineReason.NoProviderTakesAudio),
                Text = "The server makes the text, and the audio stays on the server, because no connected model provider takes audio. Anthropic takes no audio. To send audio out, an Admin connects an OpenAI-compatible provider.",
            };
    }
}

public sealed class GetTranscriptionEngineEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapGet("/transcription-engine", Handle)
        .WithName("GetTranscriptionEngine")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Gets the engine that makes the text of the next Recording of the caller, with the reason for a person.
    /// </summary>
    public static async Task<Ok<TranscriptionEngineDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new GetTranscriptionEngine.Query(), cancellationToken));
}

public static class ChooseTranscriptionEngine
{
    public sealed record Command(TranscriptionEngineChoiceDto Dto) : IRequest<TranscriptionEngineDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions, TimeProvider timeProvider)
        : IRequestHandler<Command, TranscriptionEngineDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<TranscriptionEngineDto> Handle(Command request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);
            Activity.Current?.SetTag("app.model_provider_id", request.Dto.ModelProviderId);

            string? chosenId = null;
            if (request.Dto.ModelProviderId is { } id)
            {
                var provider = await dbContext.ModelProviders
                    .TagWithOperationCallSite("model_providers.transcription.load")
                    .GetById(id, cancellationToken);
                provider.ChooseForTranscription(request.Dto.Model, timeProvider.GetUtcNow());
                chosenId = provider.Id;
            }

            var others = await dbContext.ModelProviders
                .TagWithOperationCallSite("model_providers.transcription.others")
                .Where(p => p.TranscriptionChosenOn != null && p.Id != chosenId)
                .ToListAsync(cancellationToken);
            foreach (var other in others)
                other.StopTranscription();

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Here().Information("Chose model provider {ModelProviderId} for audio", chosenId);
            return await GetTranscriptionEngine.TranscriptionEngineOf(dbContext, cancellationToken);
        }
    }
}

public sealed class ChooseTranscriptionEngineEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/transcription-engine", Handle)
        .WithName("ChooseTranscriptionEngine")
        .WithTags("ModelProviders")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Sends the audio of each new Recording to a model provider that takes audio, or keeps it on the server with no provider.
    /// A provider of a kind that takes no audio gets 422.
    /// </summary>
    public static async Task<Ok<TranscriptionEngineDto>> Handle(
        TranscriptionEngineChoiceDto dto, IMediator mediator, CancellationToken cancellationToken)
        => TypedResults.Ok(await mediator.Send(new ChooseTranscriptionEngine.Command(dto), cancellationToken));
}
