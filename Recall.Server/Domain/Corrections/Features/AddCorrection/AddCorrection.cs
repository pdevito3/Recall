namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Resources;
using Serilog;

public static class AddCorrection
{
    public sealed record Command(CorrectionForCreationDto Dto) : IRequest<CorrectionDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, CorrectionDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<CorrectionDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();

            var correction = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var added = Correction.Create(owner.TenantId, owner.UserId, request.Dto.CanonicalTerm, request.Dto.Mishearings, request.Dto.HardReplace);
                await dbContext.Corrections.AddAsync(added, ct);
                await dbContext.SaveChangesAsync(ct);
                return added;
            }, cancellationToken);

            Activity.Current?.SetTag("app.correction_id", correction.Id);
            _logger.Here().Information("Added correction {CorrectionId}", correction.Id);
            return correction.ToCorrectionDto();
        }
    }
}
