namespace Recall.Server.Domain.UserSettings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class ChangeImportReview
{
    public const string WebSessionMessage = "Only you can change the import review, in Recall in a browser.";

    public sealed record Command(ImportReviewChangeDto Dto) : IRequest<UserSettingDto>;

    public sealed class Handler(AppDbContext dbContext, ICurrentUserService currentUserService) : IRequestHandler<Command, UserSettingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserSettingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            if (!SessionClaims.IsWebSession(currentUserService.User))
                throw new ForbiddenAccessException(WebSessionMessage);
            var owner = dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.user_id", owner.UserId);

            var applied = false;
            var setting = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var stored = await dbContext.UserSettings
                    .TagWithOperationCallSite("user_settings.import_review.load")
                    .FirstOrDefaultAsync(ct);
                var row = stored ?? UserSetting.DefaultsFor(owner.TenantId, owner.UserId);

                applied = row.ChangeImportReview(request.Dto.RequireImportReview, request.Dto.Version);
                if (!applied)
                    return row;

                if (stored is null)
                    await dbContext.UserSettings.AddAsync(row, ct);
                else
                    dbContext.MoveVersionOf(row);

                await dbContext.SaveChangesAsync(ct);
                return row;
            }, cancellationToken);

            _logger.Here().Information("Took an import review change of user {UserId}, applied {Applied}, on {RequireImportReview}",
                owner.UserId, applied, setting.RequireImportReview);
            return setting.ToUserSettingDto();
        }
    }
}
