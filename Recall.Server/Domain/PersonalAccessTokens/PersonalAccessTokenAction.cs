namespace Recall.Server.Domain.PersonalAccessTokens;

using Ardalis.SmartEnum;

/// <summary>A command that a caller can send for a personal access token. A button in the SPA reads this list.</summary>
public partial class PersonalAccessTokenAction : ValueObject, IFixedNames
{
    private readonly PersonalAccessTokenActionEnum _action;

    private PersonalAccessTokenAction(PersonalAccessTokenActionEnum action) => _action = action;

    public string Value => _action.Name;

    public override string ToString() => Value;

    public static List<string> ListNames() => PersonalAccessTokenActionEnum.List.Select(x => x.Name).ToList();

    public static PersonalAccessTokenAction Revoke() => new(PersonalAccessTokenActionEnum.Revoke);

    private sealed class PersonalAccessTokenActionEnum : SmartEnum<PersonalAccessTokenActionEnum>
    {
        public static readonly PersonalAccessTokenActionEnum Revoke = new(nameof(Revoke), 0);

        private PersonalAccessTokenActionEnum(string name, int value) : base(name, value) { }
    }
}
