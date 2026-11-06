namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Content;
using Databases;
using Dtos;
using Exceptions;
using Jobs;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using Resources;
using Serilog;
using UserSettings;

public static class StartCleanup
{
    public sealed record Command(string Id) : IRequest<SessionDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, SessionDto>
    {
        private const string CleanupField = "cleanup";

        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SessionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.session_id", request.Id);

            var session = await dbContext.Sessions
                .TagWithOperationCallSite("sessions.start_cleanup.load")
                .GetById(request.Id, cancellationToken);

            // A second person or a second tab joins the run, so one Session has one result at a time.
            if (session.CleanupRun != CleanupRunState.Running)
            {
                ValidationException.MustNot(string.IsNullOrWhiteSpace(ProseMirrorToPlainText.Render(session.Raw.Text)), CleanupField, "This Session has no words to clean up.");

                var hasModel = await dbContext.ModelProviders
                    .TagWithOperationCallSite("sessions.start_cleanup.provider")
                    .NewestActivationFirst()
                    .Select(p => p.Model)
                    .FirstOrDefaultAsync(cancellationToken) is not null;
                ValidationException.Must(hasModel, CleanupField, "An Admin must connect a model provider and pick a model first.");

                var runId = session.BeginCleanup();
                await dbContext.SaveAndEnqueueAsync(
                    async (jobs, ct) => await jobs.EnqueueAsync(new CleanupSession(session.Id, session.UserId, session.TenantId, runId), DateTimeOffset.UtcNow, ct),
                    cancellationToken);
                _logger.Here().Information("Started Cleanup {CleanupRunId} of session {SessionId}", runId, session.Id);
            }

            return session.ToSessionDto(await dbContext.LoadJournalZoneAsync(cancellationToken));
        }
    }
}
