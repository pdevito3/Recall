namespace Recall.Server.Domain.UserSettings.Mappings;

using Dtos;
using Riok.Mapperly.Abstractions;

[Mapper]
public static partial class UserSettingMapper
{
    [MapperIgnoreSource(nameof(UserSetting.Id))]
    [MapperIgnoreSource(nameof(UserSetting.TenantId))]
    [MapperIgnoreSource(nameof(UserSetting.UserId))]
    [MapperIgnoreSource(nameof(UserSetting.FastClaims))]
    [MapperIgnoreSource(nameof(UserSetting.LastModifiedOn))]
    [MapperIgnoreSource(nameof(UserSetting.IsDeleted))]
    [MapperIgnoreSource(nameof(UserSetting.DomainEvents))]
    public static partial UserSettingDto ToUserSettingDto(this UserSetting setting);
}
