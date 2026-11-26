namespace Recall.Server.Domain.PersonalAccessTokens.Mappings;

using Auth;
using Dtos;
using Users;

public static class PersonalAccessTokenMapper
{
    /// <summary>The DTO with the actions that <paramref name="caller"/> can send for the token at <paramref name="now"/>.</summary>
    public static PersonalAccessTokenDto ToPersonalAccessTokenDto(this PersonalAccessTokenRow row, Caller caller, DateTimeOffset now) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        Name = row.Name,
        Permissions = row.Grant.Permissions.Select(p => p.Value).Order(StringComparer.Ordinal).ToList(),
        ReadJournal = row.Grant.Journal.CanRead,
        WriteJournal = row.Grant.Journal.CanWrite,
        JobTools = row.Grant.JobTools,
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
        LastUsedAt = row.LastUsedAt,
        RevokedAt = row.RevokedAt,
        AllowedActions = PersonalAccessTokenAction.AllowedFor(caller, row, now).Select(a => a.Value).ToList(),
    };
}
