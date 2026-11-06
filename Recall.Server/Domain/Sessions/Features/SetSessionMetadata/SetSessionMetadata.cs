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

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, SessionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.set_metadata.load")
                .GetById(request.Id, cancellationToken);
            var applied = session.SetMetadata(SessionMetadata.Of(request.Dto.Activity, request.Dto.Moods, request.Dto.UserTopics), request.Dto.MetadataVersion);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Took a metadata change of session {SessionId}, applied {Applied}", session.Id, applied);
            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }
    }
}
