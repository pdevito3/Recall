namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Cleanup;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Resources;
using Serilog;
using UserSettings;

public static class RecordCleanupResult
{
    public sealed record Command(string Id, CleanupResultDto Dto) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, CleanupCompletion completion, TimeProvider timeProvider) : IRequestHandler<Command, SessionDto>
    {
        private const string BaseField = "baseRawRevision";
        private const string CleanedField = "cleanedMarkdown";
        private const string UnusableText = "The result of this Cleanup is empty, too long, or has a character that is not allowed.";

        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.record_cleanup_result.load")
                .GetById(request.Id, cancellationToken);

            var dto = request.Dto;
            ValidationException.Must(dto.BaseRawRevision >= 1 && dto.BaseRawRevision <= session.Raw.LatestRevision, BaseField, "The base Raw Revision is not a Revision of this Session.");

            var output = CleanupOutput.Checked(dto.CleanedMarkdown, dto.Synopsis, dto.TopicSuggestions, dto.PeopleProposal, dto.MoodSuggestions);
            ValidationException.Must(output is not null, CleanedField, UnusableText);

            var outcome = await completion.OutcomeOfAsync(session, output!, cancellationToken);
            ValidationException.Must(outcome is not null, CleanedField, UnusableText);

            // A retry of a save that reached the server gets the stored Session and makes no second Revision.
            var isReplay = session.CleanupRun == CleanupRunState.Clean
                && session.LastCleanupRawRevision == dto.BaseRawRevision
                && session.Cleaned.Text == outcome!.CleanedText;
            if (!isReplay)
            {
                session.BeginCleanup();
                session.CompleteCleanup(outcome!, dto.BaseRawRevision, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.Here().Information("Took a Cleanup result from a device for session {SessionId}", session.Id);
            }

            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }
    }
}
