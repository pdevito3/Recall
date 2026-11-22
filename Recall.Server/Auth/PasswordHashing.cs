namespace Recall.Server.Auth;

using Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public enum PasswordCheck
{
    Wrong,
    Right,

    /// <summary>The password is right, and its hash has a lower cost than the Server uses now (PWD-10).</summary>
    RightAndRehash,
}

/// <summary>
/// Each password hash of the Server (PWD-10, PWD-11). <see cref="PasswordHasher{TUser}"/> of the shared framework writes
/// PBKDF2-HMAC-SHA512 with a 128-bit salt, and its format names its own cost. At most <see cref="MaxAtOnce"/> hashes run
/// at once, so a flood of logins cannot take each core. A call that gets no slot in <see cref="SlotWait"/> gets null, and
/// the caller answers 429.
/// </summary>
public sealed class PasswordHashing : IDisposable
{
    public const int IterationCount = 220_000;
    public const int MaxAtOnce = 2;

    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(5);

    // A login name that no credential holds checks the password against this hash, so its answer takes as long as a wrong password.
    private const string DummyPassword = "recall-no-such-login";

    private readonly IPasswordHasher<PasswordCredential> _hasher;
    private readonly TimeSpan _slotWait;
    private readonly SemaphoreSlim _slots = new(MaxAtOnce, MaxAtOnce);
    private readonly Lazy<string> _dummyHash;

    public PasswordHashing()
        : this(new PasswordHasher<PasswordCredential>(Options.Create(new PasswordHasherOptions { IterationCount = IterationCount })), SlotWait)
    {
    }

    internal PasswordHashing(IPasswordHasher<PasswordCredential> hasher, TimeSpan slotWait)
    {
        _hasher = hasher;
        _slotWait = slotWait;
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(null!, DummyPassword));
    }

    /// <summary>The hash to store, or null when no slot came free in time.</summary>
    public Task<string?> HashAsync(string password, CancellationToken cancellationToken)
        => InSlotAsync(() => _hasher.HashPassword(null!, password), cancellationToken);

    /// <summary>
    /// Checks a password against a stored hash, or against a fixed hash when <paramref name="storedHash"/> is null (PWD-6).
    /// Null when no slot came free in time.
    /// </summary>
    public async Task<PasswordCheck?> CheckAsync(string? storedHash, string password, CancellationToken cancellationToken)
    {
        var result = await InSlotAsync<PasswordVerificationResult?>(
            () => _hasher.VerifyHashedPassword(null!, storedHash ?? _dummyHash.Value, password), cancellationToken);
        return result switch
        {
            null => null,
            _ when storedHash is null => PasswordCheck.Wrong,
            PasswordVerificationResult.Success => PasswordCheck.Right,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.RightAndRehash,
            _ => PasswordCheck.Wrong,
        };
    }

    public void Dispose() => _slots.Dispose();

    private async Task<T?> InSlotAsync<T>(Func<T> hash, CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(_slotWait, cancellationToken))
            return default;
        try
        {
            return hash();
        }
        finally
        {
            _slots.Release();
        }
    }
}
