namespace Recall.Server.Domain.UserSettings.Mappings;

using Dtos;

public static class UserSettingMapper
{
    public static UserSettingDto ToUserSettingDto(this UserSetting setting) => new()
    {
        TimeZoneMode = setting.TimeZone.Mode.ToString(),
        TimeZone = setting.TimeZone.Zone,
        TimeZoneIsKnown = setting.TimeZoneIsKnown,
        LocationCaptureEnabled = setting.LocationCaptureEnabled,
        RequirePeopleProposalApproval = setting.RequirePeopleProposalApproval,
        RemoveFillerWords = setting.RemoveFillerWords,
        WriteNumbersAsDigits = setting.WriteNumbersAsDigits,
        Version = setting.Version,
    };
}
