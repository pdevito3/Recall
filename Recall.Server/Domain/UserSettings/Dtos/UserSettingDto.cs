namespace Recall.Server.Domain.UserSettings.Dtos;

public sealed record UserSettingDto
{
    public string? TimeZone { get; init; }
    public bool LocationCaptureEnabled { get; init; }
    public bool RequirePeopleTagApproval { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    public uint Version { get; init; }
}
