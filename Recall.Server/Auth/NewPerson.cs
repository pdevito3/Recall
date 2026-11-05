namespace Recall.Server.Auth;

using Databases;
using Domain.EmailAddresses;
using Domain.Users;

/// <summary>The profile claims of a provider. Any of them can be absent or odd.</summary>
public sealed record Profile(string? GivenName, string? FamilyName, string? Name, string? Email, string? PreferredUsername)
{
    public override string ToString() => "[profile]";
}

/// <summary>A person whom a provider has proved. The subject comes only from the ID token.</summary>
public sealed record ExternalIdentity(string Issuer, string Subject, Profile Profile)
{
    public override string ToString() => "[external identity]";
}

/// <summary>The values of a new user. They always pass the rules of <see cref="User"/>, so an odd claim never fails a login.</summary>
public sealed record NewPerson(string FirstName, string? LastName, string? Email, string Username)
{
    private const string FallbackFirstName = "Recall user";

    public static NewPerson FromClaims(Profile profile)
    {
        var name = Clean(profile.Name, int.MaxValue);
        var nameParts = name?.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var email = EmailOf(profile);
        var localPart = email?[..email.IndexOf('@', StringComparison.Ordinal)];
        var username = Clean(profile.PreferredUsername, User.UsernameMaxLength);

        var given = Clean(profile.GivenName, User.FirstNameMaxLength);
        var firstName = given
            ?? Clean(nameParts?[0], User.FirstNameMaxLength)
            ?? Clean(username, User.FirstNameMaxLength)
            ?? Clean(localPart, User.FirstNameMaxLength)
            ?? FallbackFirstName;

        var lastName = LastNameOf(profile)
            ?? (given is null && nameParts is { Length: 2 } ? Clean(nameParts[1], User.LastNameMaxLength) : null);

        return new NewPerson(firstName, lastName, email, username ?? Clean(email, User.UsernameMaxLength) ?? Clean(firstName, User.UsernameMaxLength)!);
    }

    /// <summary>The last name that the provider states, with no fallback. A repeat login fills an empty value with it.</summary>
    public static string? LastNameOf(Profile profile) => Clean(profile.FamilyName, User.LastNameMaxLength);

    /// <summary>The valid email that the provider states, with no fallback. A repeat login fills an empty value with it.</summary>
    public static string? EmailOf(Profile profile)
    {
        var email = Clean(profile.Email, int.MaxValue);
        if (email is null || email.Length > EmailAddress.MaxLength)
            return null;

        try
        {
            return EmailAddress.Of(email).Value;
        }
        catch (Exception exception) when (exception is Exceptions.ValidationException or FluentValidation.ValidationException)
        {
            return null;
        }
    }

    public override string ToString() => "[new person]";

    private static string? Clean(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !StorableText.CanStore(text))
            return null;
        if (text.Length <= maxLength)
            return text;

        // A cut between the two halves of a surrogate pair leaves text that the column cannot store.
        var clipped = char.IsHighSurrogate(text[maxLength - 1]) ? text[..(maxLength - 1)] : text[..maxLength];
        return clipped.TrimEnd();
    }
}
