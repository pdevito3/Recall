namespace Recall.Server.Domain.UserSettings.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetUserSetting
{
    public sealed record Query : IRequest<UserSettingDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, UserSettingDto>
    {
        public async Task<UserSettingDto> Handle(Query request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();

            var setting = await dbContext.UserSettings
                .TagWithOperationCallSite("user_settings.get")
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            return (setting ?? UserSetting.DefaultsFor(owner.TenantId, owner.UserId)).ToUserSettingDto();
        }
    }
}
