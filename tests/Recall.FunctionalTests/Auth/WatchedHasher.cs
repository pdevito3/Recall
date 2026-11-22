namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

/// <summary>
/// The hasher of each test host. It counts the checks of each typed password, and it can hold the first check of one
/// password, so a test can commit a rival write between the load of a credential and its save. The tests of a host run at
/// one time, so each test watches a password that only it types.
/// </summary>
public sealed class WatchedHasher : IPasswordHasher<PasswordCredential>
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);

    private readonly PasswordHasher<PasswordCredential> _hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = PasswordHashing.IterationCount }));

    private readonly ConcurrentDictionary<string, int> _checks = new();
    private readonly ConcurrentDictionary<string, Hold> _holds = new();
    private readonly ConcurrentDictionary<string, int> _hashes = new();

    /// <summary>The checks of a typed password against a hash, also against the fixed hash of PWD-6.</summary>
    public int ChecksOf(string password) => _checks.GetValueOrDefault(password);

    /// <summary>The hashes made of a password, as for a new password.</summary>
    public int HashesOf(string password) => _hashes.GetValueOrDefault(password);

    /// <summary>Holds the next check of <paramref name="password"/> until the test releases it.</summary>
    public Hold HoldNextCheckOf(string password) => _holds[password] = new Hold();

    public string HashPassword(PasswordCredential user, string password)
    {
        _hashes.AddOrUpdate(password, 1, (_, count) => count + 1);
        return _hasher.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(PasswordCredential user, string hashedPassword, string providedPassword)
    {
        _checks.AddOrUpdate(providedPassword, 1, (_, count) => count + 1);
        if (_holds.TryRemove(providedPassword, out var hold))
        {
            hold.Reached.TrySetResult();
            // The check runs in a slot of PasswordHashing, on a thread of its own, so the test blocks only that thread.
            if (!hold.Released.Task.Wait(HoldLimit))
                throw new TimeoutException($"The test did not release the check of a password within {HoldLimit}.");
        }

        return _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    public sealed class Hold
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReachedAsync() => Reached.Task.WaitAsync(HoldLimit);

        public void Release() => Released.TrySetResult();
    }
}
