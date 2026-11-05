namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using MediatR;
using Resources;
using Serilog;

public static class DeleteCorrection
{
    public sealed record Command(string Id) : IRequest;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.correction_id", request.Id);

            var correction = await dbContext.Corrections
                .TagWithOperationCallSite("corrections.delete.load")
                .GetById(request.Id, cancellationToken);
            dbContext.Corrections.Remove(correction);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Deleted correction {CorrectionId}", correction.Id);
        }
    }
}
