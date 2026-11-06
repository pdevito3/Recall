namespace Recall.Server.Domain.Corrections.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetCorrection
{
    public sealed record Query(string Id) : IRequest<CorrectionDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, CorrectionDto>
    {
        public async Task<CorrectionDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.correction_id", request.Id);

            var correction = await dbContext.Corrections
                .TagWithOperationCallSite("corrections.get")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            return correction.ToCorrectionDto();
        }
    }
}
