namespace Recall.Server.Domain.People;

using Databases;
using Exceptions;

public class Person : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "per";
    public const int LabelMaxLength = 200;

    private const string LabelField = "label";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string Label { get; private set; } = default!;

    /// <summary>
    /// The name of a Person that a Cleanup or an answer to a proposal added. The database holds one of each for a user, so a race
    /// of these adds gives one Person. A rename clears it, and a Person that a person asks for has none, because a second Person
    /// of one name is a real choice.
    /// </summary>
    [Sensitive] public string? ProposalKey { get; private set; }

    public static Person Create(string tenantId, string userId, string label)
    {
        var person = new Person { TenantId = tenantId, UserId = userId };
        person.Rename(label);
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

    /// <summary>A delete runs again when a Session save races it, so a stale version that the caller saw must not run it again.</summary>
    public void GuardDelete(uint? ifVersion)
    {
        if (ifVersion is { } version && version != Version)
            throw new StaleSeenVersionException();
    }

    protected Person() : base(IdPrefix) { }
}
