namespace Recall.Server.Domain.People;

using Exceptions;

public class Person : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "per";
    public const int LabelMaxLength = 200;

    private const string LabelField = "label";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string Label { get; private set; } = default!;

    public static Person Create(string tenantId, string userId, string label)
    {
        var person = new Person { TenantId = tenantId, UserId = userId };
        person.Rename(label);
        return person;
    }

    public void Rename(string label)
    {
        ValidationException.ThrowWhenNullOrWhitespace(label, LabelField, "Please provide a name.");
        var trimmed = label.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, LabelMaxLength, LabelField, $"The name must not have more than {LabelMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, LabelField, "The name has a character that is not allowed.");
        Label = trimmed;
    }

    protected Person() : base(IdPrefix) { }
}
