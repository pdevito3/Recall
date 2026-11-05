namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Metadata;
using Resources;
using Serilog;
using UserSettings;

public static class SetSessionMetadata
{
    public sealed record Command(string Id, SessionMetadataChangeDto Dto) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, SessionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.set_metadata.load")
                .GetById(request.Id, cancellationToken);
            session.SetMetadata(SessionMetadata.Of(request.Dto.Activity, request.Dto.Moods, request.Dto.UserTopics), request.Dto.SavedAt, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Set the metadata of session {SessionId}", session.Id);
            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }
    }
}
