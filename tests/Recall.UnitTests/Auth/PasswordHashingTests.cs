namespace Recall.UnitTests.Auth;

using Microsoft.AspNetCore.Identity;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Shouldly;

public sealed class PasswordHashingTests
{
    [Test]
    [Property("Rule", "PWD-11")]
    public async Task a_third_hash_waits_for_a_slot_and_gets_none_when_two_hashes_hold_them()
    {
        // Arrange
        using var release = new ManualResetEventSlim();
        var hasher = new HeldHasher(release);
        using var hashing = new PasswordHashing(hasher, TimeSpan.FromMilliseconds(200));
        var first = Task.Run(() => hashing.HashAsync("one", CancellationToken.None));
        var second = Task.Run(() => hashing.HashAsync("two", CancellationToken.None));
        while (hasher.Running < PasswordHashing.MaxAtOnce)
            await Task.Delay(10);

        // Act
        var third = await hashing.CheckAsync("stored", "three", CancellationToken.None);
        release.Set();

        // Assert
        third.ShouldBeNull();
        (await first).ShouldBe("hash");
        (await second).ShouldBe("hash");
    }

    [Test]
    [Property("Rule", "PWD-10")]
    public async Task a_hash_uses_pbkdf2_at_220000_iterations_and_reports_a_lower_cost_as_a_rehash()
    {
        // Arrange
        using var hashing = new PasswordHashing();
        var low = new PasswordHasher<PasswordCredential>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 1000 }))
            .HashPassword(null!, "a long password phrase");

        // Act
        var hash = await hashing.HashAsync("a long password phrase", CancellationToken.None);
        var right = await hashing.CheckAsync(hash, "a long password phrase", CancellationToken.None);
        var rehash = await hashing.CheckAsync(low, "a long password phrase", CancellationToken.None);
        var wrong = await hashing.CheckAsync(hash, "another phrase", CancellationToken.None);
        var unknown = await hashing.CheckAsync(null, "recall-no-such-login", CancellationToken.None);

        // Assert - the V3 format: a marker, the PRF (2 is HMAC-SHA512), the iterations, and a salt of 16 bytes
        var bytes = Convert.FromBase64String(hash!);
        bytes[0].ShouldBe((byte)0x01);
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)).ShouldBe(2u);
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)).ShouldBe((uint)PasswordHashing.IterationCount);
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9)).ShouldBe(16u);
        right.ShouldBe(PasswordCheck.Right);
        rehash.ShouldBe(PasswordCheck.RightAndRehash);
        wrong.ShouldBe(PasswordCheck.Wrong);
        unknown.ShouldBe(PasswordCheck.Wrong);
    }

    private sealed class HeldHasher(ManualResetEventSlim release) : IPasswordHasher<PasswordCredential>
    {
        private int _running;

        public int Running => Volatile.Read(ref _running);

        public string HashPassword(PasswordCredential user, string password)
        {
            if (password == "recall-no-such-login")
                return "dummy";
            Interlocked.Increment(ref _running);
            release.Wait();
            return "hash";
        }

        public PasswordVerificationResult VerifyHashedPassword(PasswordCredential user, string hashedPassword, string providedPassword)
            => PasswordVerificationResult.Failed;
    }
}
