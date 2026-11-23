namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Resources;
using Serilog;

public static class RespondToSuggestion
{
    public sealed record Command(string Id, SuggestionResponseDto Dto, bool Accept) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, SessionDto>
    {
        private const string KindField = "kind";

        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var isKnown = Enum.TryParse<SuggestionKind>(request.Dto.Kind, out var kind) && Enum.IsDefined(kind);
            ValidationException.Must(isKnown, KindField, "A suggestion must be a Topic or a Mood.");

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.respond_to_suggestion.load")
                .GetById(request.Id, cancellationToken);

            var changed = (kind, request.Accept) switch
            {
                (SuggestionKind.Topic, true) => session.AcceptTopicSuggestion(request.Dto.Value),
                (SuggestionKind.Topic, false) => session.RejectTopicSuggestion(request.Dto.Value),
                (SuggestionKind.Mood, true) => session.AcceptMoodSuggestion(request.Dto.Value),
                (SuggestionKind.Mood, false) => session.RejectMoodSuggestion(request.Dto.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Took a response to a suggestion of session {SessionId}, kind {SuggestionKind}, accept {Accept}, changed {Changed}", session.Id, kind, request.Accept, changed);
            return session.ToSessionDto(await ProposalPeople.LoadAsync(dbContext, [session], cancellationToken));
        }
    }
}
