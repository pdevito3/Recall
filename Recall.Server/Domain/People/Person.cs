namespace Recall.Server.Domain.People;

using Databases;
using Exceptions;

public class Person : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "per";
    public const int LabelMaxLength = 200;
    public const int FullNameMaxLength = 200;
    public const int NotesMaxLength = 1000;

    private const string LabelField = "label";
    private const string FullNameField = "fullName";
    private const string NotesField = "notes";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string Label { get; private set; } = default!;

    /// <summary>
    /// The name of a Person that a Cleanup or an answer to a proposal added. The database holds one of each for a user, so a race
    /// of these adds gives one Person. A rename clears it, and a Person that a person asks for has none, because a second Person
    /// of one name is a real choice.
    /// </summary>
    [Sensitive] public string? ProposalKey { get; private set; }

    /// <summary>A detail that tells two People of one label apart, such as "Jane Park". A mention never shows it.</summary>
    [Sensitive] public string? FullName { get; private set; }

    /// <summary>Free words of the person about this Person, such as "Sam's teacher". A mention never shows them.</summary>
    [Sensitive] public string? Notes { get; private set; }

    public static Person Create(string tenantId, string userId, string label, string? fullName = null, string? notes = null)
    {
        var person = new Person { TenantId = tenantId, UserId = userId };
        person.Rename(label);
        person.ApplyDetails(fullName, notes);
        return person;
    }

    public static Person CreateFromProposedName(string tenantId, string userId, string name)
    {
        var person = Create(tenantId, userId, name);
        person.ProposalKey = KeyOf(person.Label);
        return person;
    }

    public static string KeyOf(string name) => VisibleText.OneLine(name).ToUpperInvariant();

    public void Rename(string label)
    {
        ValidationException.ThrowWhenNullOrWhitespace(label, LabelField, "Please provide a name.");
        ValidationException.ThrowWhenHasCharacterNotStorable(label, LabelField, "The name has a character that is not allowed.");
        var trimmed = VisibleText.OneLine(label);
        ValidationException.ThrowWhenNullOrWhitespace(trimmed, LabelField, "Please provide a name.");
        ValidationException.ThrowWhenLongerThan(trimmed, LabelMaxLength, LabelField, $"The name must not have more than {LabelMaxLength} characters.");
        Label = trimmed;
        ProposalKey = null;
    }

    /// <summary>
    /// Sets the details that tell People of one label apart. The label, the proposal key, and each mention stay as they are.
    /// A blank detail becomes no detail.
    /// </summary>
    public void Describe(string? fullName, string? notes, uint version)
    {
        GuardVersion(version);
        ApplyDetails(fullName, notes);
    }

    private void ApplyDetails(string? fullName, string? notes)
    {
        string? full = null;
        if (fullName is not null)
        {
            ValidationException.ThrowWhenHasCharacterNotStorable(fullName, FullNameField, "The full name has a character that is not allowed.");
            full = VisibleText.OneLine(fullName);
            ValidationException.ThrowWhenLongerThan(full, FullNameMaxLength, FullNameField, $"The full name must not have more than {FullNameMaxLength} characters.");
        }

        string? note = null;
        if (notes is not null)
        {
            ValidationException.ThrowWhenHasCharacterNotStorable(notes, NotesField, "The notes have a character that is not allowed.");
            note = VisibleText.Without(notes).Trim();
            ValidationException.ThrowWhenLongerThan(note, NotesMaxLength, NotesField, $"The notes must not have more than {NotesMaxLength} characters.");
        }

        FullName = string.IsNullOrEmpty(full) ? null : full;
        Notes = string.IsNullOrEmpty(note) ? null : note;
    }

    /// <summary>A delete runs again when a Session save races it, so a stale version that the caller saw must not run it again.</summary>
    public void GuardDelete(uint? ifVersion)
    {
        if (ifVersion is { } version && version != Version)
            throw new StaleSeenVersionException();
    }

    protected Person() : base(IdPrefix) { }
}
