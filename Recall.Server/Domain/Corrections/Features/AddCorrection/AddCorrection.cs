namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class AddCorrection
{
    public sealed record Result(CorrectionDto Correction, bool IsNew);

    public sealed record Command(CorrectionForCreationDto Dto) : IRequest<Result>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, Result>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var dto = request.Dto;

            var result = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                if (dto.Id is not null && EntityId.IsValid(dto.Id, Correction.IdPrefix))
                {
                    var existing = await dbContext.Corrections
                        .TagWithOperationCallSite("corrections.add.replay")
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == dto.Id, ct);
                    if (existing is not null)
                        return new Result(existing.ToCorrectionDto(), false);
                }

                var added = Correction.Create(owner.TenantId, owner.UserId, dto.CanonicalTerm, dto.Mishearings, dto.HardReplace, dto.Id);
                await dbContext.Corrections.AddAsync(added, ct);
                await dbContext.SaveChangesAsync(ct);
                return new Result(added.ToCorrectionDto(), true);
            }, cancellationToken);

            Activity.Current?.SetTag("app.correction_id", result.Correction.Id);
            Activity.Current?.SetTag("app.correction.is_new", result.IsNew);
            _logger.Here().Information("Added correction {CorrectionId}, new {IsNew}", result.Correction.Id, result.IsNew);
            return result;
        }
    }
}
