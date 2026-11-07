namespace Recall.Server.Domain.ModelProviders;

/// <summary>
/// One value under the key ring, so that a start sees a new ring before a provider key needs the old one. It holds a fixed
/// word and no secret.
/// </summary>
[NotAudited("It holds a fixed word under the key ring, and only the start of the server writes it.")]
public class KeyRingMarker
{
    public const int SingleId = 1;
    public const int ProtectedValueMaxLength = 1_000;

    public int Id { get; private set; } = SingleId;

    public string ProtectedValue { get; set; } = default!;
}
