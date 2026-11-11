namespace Recall.Server.Domain.Corrections;

using Exceptions;

public class Correction : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "cor";
    public const int CanonicalTermMaxLength = 200;
    public const int MishearingMaxLength = 200;
    public const int MishearingsMaxCount = 100;

    // A JSON list of the longest values, with room for the quotes and the commas, and for the escapes of each character.
    public const int MishearingsColumnMaxLength = MishearingsMaxCount * (MishearingMaxLength * 6 + 3);

    private const string CanonicalTermField = "canonicalTerm";
    private const string MishearingsField = "mishearings";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string CanonicalTerm { get; private set; } = default!;

    [Sensitive] public IReadOnlyList<string> Mishearings { get; private set; } = [];

    public bool HardReplace { get; private set; }

    public static Correction Create(string tenantId, string userId, string canonicalTerm, IEnumerable<string?>? mishearings, bool hardReplace, string? id = null)
    {
        var correction = new Correction { TenantId = tenantId, UserId = userId };
        if (id is not null)
            correction.SetId(id);
        correction.Apply(canonicalTerm, mishearings, hardReplace);
        return correction;
    }

    public void Update(string canonicalTerm, IEnumerable<string?>? mishearings, bool hardReplace, uint version)
    {
        GuardVersion(version);
        Apply(canonicalTerm, mishearings, hardReplace);
    }

    private void Apply(string canonicalTerm, IEnumerable<string?>? mishearings, bool hardReplace)
    {
        ValidationException.ThrowWhenNullOrWhitespace(canonicalTerm, CanonicalTermField, "Please provide a term.");
        var term = canonicalTerm.Trim();
        ValidationException.ThrowWhenLongerThan(term, CanonicalTermMaxLength, CanonicalTermField, $"The term must not have more than {CanonicalTermMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(term, CanonicalTermField, "The term has a character that is not allowed.");

        var heard = (mishearings ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
            .Where(m => !m.Equals(term, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ValidationException.Must(heard.Count <= MishearingsMaxCount, MishearingsField, $"A term must not have more than {MishearingsMaxCount} mishearings.");
        foreach (var mishearing in heard)
        {
            ValidationException.ThrowWhenLongerThan(mishearing, MishearingMaxLength, MishearingsField, $"A mishearing must not have more than {MishearingMaxLength} characters.");
            ValidationException.ThrowWhenHasCharacterNotStorable(mishearing, MishearingsField, "A mishearing has a character that is not allowed.");
        }

        CanonicalTerm = term;
        Mishearings = heard;
        HardReplace = hardReplace;
    }

    protected Correction() : base(IdPrefix) { }
}
