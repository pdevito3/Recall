namespace Recall.Server.Domain.PersonalAccessTokens;

using Ardalis.SmartEnum;
using Auth;
using Users;

/// <summary>A command that a caller can send for a personal access token. A button in the SPA reads this list.</summary>
public partial class PersonalAccessTokenAction : ValueObject, IFixedNames
{
    private readonly PersonalAccessTokenActionEnum _action;

    private PersonalAccessTokenAction(PersonalAccessTokenActionEnum action) => _action = action;

    public string Value => _action.Name;

    public override string ToString() => Value;

    public static List<string> ListNames() => PersonalAccessTokenActionEnum.List.Select(x => x.Name).ToList();

    public static PersonalAccessTokenAction Revoke() => new(PersonalAccessTokenActionEnum.Revoke);

    /// <summary>The person of the token, and a caller with <c>manage_user_access</c> in its tenant, can revoke it.</summary>
    internal static bool CanRevoke(Caller caller, string userId)
        => caller.IsUser(userId) || caller.Has(Permission.ManageUserAccess());

    internal static IReadOnlyList<PersonalAccessTokenAction> AllowedFor(Caller caller, PersonalAccessTokenRow row, DateTimeOffset now)
        => row.RevokedAt is null && (row.ExpiresAt is not { } end || end > now) && CanRevoke(caller, row.UserId) ? [Revoke()] : [];

    private sealed class PersonalAccessTokenActionEnum : SmartEnum<PersonalAccessTokenActionEnum>
    {
        public static readonly PersonalAccessTokenActionEnum Revoke = new(nameof(Revoke), 0);

        private PersonalAccessTokenActionEnum(string name, int value) : base(name, value) { }
    }
}
