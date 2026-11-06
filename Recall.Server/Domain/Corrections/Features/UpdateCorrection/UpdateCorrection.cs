namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Resources;
using Serilog;

public static class UpdateCorrection
{
    public sealed record Command(string Id, CorrectionForUpdateDto Dto) : IRequest<CorrectionDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, CorrectionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<CorrectionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.correction_id", request.Id);

            var correction = await dbContext.Corrections
                .TagWithOperationCallSite("corrections.update.load")
                .GetById(request.Id, cancellationToken);
            correction.Update(request.Dto.CanonicalTerm, request.Dto.Mishearings, request.Dto.HardReplace, request.Dto.Version);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Updated correction {CorrectionId}", correction.Id);
            return correction.ToCorrectionDto();
        }
    }
}
