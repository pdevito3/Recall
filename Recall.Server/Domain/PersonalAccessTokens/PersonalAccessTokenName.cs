namespace Recall.Server.Domain.PersonalAccessTokens;

using Auth.Entities;
using Exceptions;

/// <summary>The name that a person gives a token, so the list tells their tokens apart.</summary>
public sealed record PersonalAccessTokenName
{
    public const string Field = "name";

    private PersonalAccessTokenName(string value) => Value = value;

    public string Value { get; }

    /// <exception cref="ValidationException">The name is empty, too long, or has a character that cannot be stored.</exception>
    public static PersonalAccessTokenName Of(string? value)
    {
        ValidationException.ThrowWhenNullOrWhitespace(value, Field, "Please give the token a name.");
        var name = value!.Trim();
        ValidationException.ThrowWhenLongerThan(name, AuthSession.NameMaxLength, Field,
            $"A name can have no more than {AuthSession.NameMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(name, Field, "A name has a character that is not allowed.");
        return new PersonalAccessTokenName(name);
    }

    public override string ToString() => Value;
}
