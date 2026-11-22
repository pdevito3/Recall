namespace Recall.Server.Auth;

using Databases;
using Domain.EmailAddresses;
using Domain.Users;

public sealed record Profile(string? GivenName, string? FamilyName, string? Name, string? Email, string? PreferredUsername)
{
    public override string ToString() => "[profile]";
}

public sealed record ExternalIdentity(string Issuer, string Subject, Profile Profile)
{
    public override string ToString() => "[external identity]";
}

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

    public static string? LastNameOf(Profile profile) => Clean(profile.FamilyName, User.LastNameMaxLength);

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

    /// <summary>The label of a link: the email that the provider gave, else the name, so the person can tell which account is linked.</summary>
    public static string? LabelOf(Profile profile)
        => EmailOf(profile) ?? Clean(profile.Name, Entities.ExternalLogin.DisplayLabelMaxLength);

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
