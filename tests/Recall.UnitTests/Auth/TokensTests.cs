namespace Recall.UnitTests.Auth;

using Recall.Server.Auth;
using Shouldly;

public sealed class TokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly string Part = new string('a', Secrets.EncodedLength - 1) + "A";

    [Test]
    public void token_hash_is_lowercase_hex_sha256()
    {
        // Act
        var hash = TokenHash.Of("abc");

        // Assert
        hash.Hex.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public void a_new_refresh_token_survives_encode_and_parse()
    {
        // Arrange
        var token = RefreshToken.New();

        // Act
        var parsed = RefreshToken.TryParse(token.Encode(), out var result);

        // Assert
        parsed.ShouldBeTrue();
        result.ShouldBe(token);
    }

    [Test]
    [MethodDataSource(nameof(BadRefreshTokens))]
    public void a_malformed_refresh_token_does_not_parse(string? raw)
    {
        // Act
        var parsed = RefreshToken.TryParse(raw, out var token);

        // Assert
        parsed.ShouldBeFalse();
        token.ShouldBeNull();
    }

    public static IEnumerable<string?> BadRefreshTokens()
    {
        yield return null;
        yield return "";
        yield return "rcl_rt_";
        yield return Part + "." + Part;
        yield return "rcl_at_" + Part + "." + Part;
        yield return "rcl_rt_" + Part;
        yield return "rcl_rt_" + Part + ".";
        yield return "rcl_rt_." + Part;
        yield return "rcl_rt_" + Part + "." + Part + "." + Part;
        yield return "rcl_rt_" + Part + "a." + Part;
        yield return "rcl_rt_" + Part[1..] + "." + Part;
        yield return "rcl_rt_" + Part[1..] + "+." + Part;
        yield return "rcl_rt_" + Part[1..] + "=." + Part;
        yield return "RCL_RT_" + Part + "." + Part;
    }

    [Test]
    public void an_absent_retry_key_is_valid_and_null()
    {
        // Act
        var parsed = RetryKey.TryParse(null, out var key);

        // Assert
        parsed.ShouldBeTrue();
        key.ShouldBeNull();
    }

    [Test]
    [Arguments(42, 'a', false)]
    [Arguments(43, 'a', true)]
    [Arguments(256, 'a', true)]
    [Arguments(257, 'a', false)]
    [Arguments(43, '+', false)]
    [Arguments(43, '=', false)]
    [Arguments(43, ' ', false)]
    [Arguments(43, '-', true)]
    [Arguments(43, '_', true)]
    public void retry_key_form_table(int length, char character, bool valid)
    {
        // Act
        var parsed = RetryKey.TryParse(new string(character, length), out var key);

        // Assert
        parsed.ShouldBe(valid);
        (key is not null).ShouldBe(valid);
    }

    [Test]
    public void derivation_is_stable_for_one_token_and_one_key()
    {
        // Arrange
        var token = RefreshToken.New();
        var key = Key();

        // Act
        var first = TokenDerivation.Derive(token, key, Now);
        var second = TokenDerivation.Derive(token, key, Now.AddHours(1));

        // Assert
        first.AccessToken.ShouldBe(second.AccessToken);
        first.RefreshToken.ShouldBe(second.RefreshToken);
        first.AccessToken.ShouldStartWith("rcl_at_");
        RefreshToken.TryParse(first.RefreshToken, out var parsed).ShouldBeTrue();
        parsed!.Family.ShouldBe(token.Family);
        first.AccessToken.ShouldNotContain(parsed.Secret);
    }

    [Test]
    public void another_key_or_another_token_derives_another_pair()
    {
        // Arrange
        var token = RefreshToken.New();
        var key = Key();

        // Act
        var pair = TokenDerivation.Derive(token, key, Now);
        var otherKey = TokenDerivation.Derive(token, Key(), Now);
        var otherToken = TokenDerivation.Derive(token with { Secret = Secrets.New() }, key, Now);

        // Assert
        otherKey.RefreshToken.ShouldNotBe(pair.RefreshToken);
        otherKey.AccessToken.ShouldNotBe(pair.AccessToken);
        otherToken.RefreshToken.ShouldNotBe(pair.RefreshToken);
        otherToken.AccessToken.ShouldNotBe(pair.AccessToken);
    }

    [Test]
    public void refresh_rule_table()
    {
        // Arrange
        var current = RefreshToken.New();
        var older = current with { Secret = Secrets.New() };
        var key = Key();
        var rowOfCurrent = new RefreshState(TokenHash.Of(current.Encode()).Hex, Now.AddDays(1), null);
        var derivedFromOlder = RefreshToken.TryParse(TokenDerivation.Derive(older, key, Now).RefreshToken, out var derived) ? derived! : throw new InvalidOperationException();
        var rowAfterRotation = rowOfCurrent with { RefreshHash = TokenHash.Of(derivedFromOlder.Encode()).Hex };
        var cases = new (string Name, RefreshState Row, RefreshToken Token, RetryKey? Key, RefreshDecision Expected)[]
        {
            ("current with no key", rowOfCurrent, current, null, RefreshDecision.Rotate),
            ("current with a key", rowOfCurrent, current, key, RefreshDecision.Rotate),
            ("older with no key", rowOfCurrent, older, null, RefreshDecision.Replay),
            ("older with a key that does not derive", rowOfCurrent, older, key, RefreshDecision.Replay),
            ("delayed copy of the rotated token and key", rowAfterRotation, older, key, RefreshDecision.RotateRetry),
            ("rotated token with no key", rowAfterRotation, older, null, RefreshDecision.Replay),
            ("rotated token with another key", rowAfterRotation, older, Key(), RefreshDecision.Replay),
            ("result of the rotation itself", rowAfterRotation, derivedFromOlder, key, RefreshDecision.Rotate),
            ("revoked comes before a match", rowOfCurrent with { RevokedAt = Now.AddDays(-1) }, current, key, RefreshDecision.Revoked),
            ("revoked comes before expired", rowOfCurrent with { RevokedAt = Now.AddDays(-1), ExpiresAt = Now.AddDays(-1) }, older, null, RefreshDecision.Revoked),
            ("expired comes before a match", rowOfCurrent with { ExpiresAt = Now.AddSeconds(-1) }, current, key, RefreshDecision.Expired),
            ("expired comes before a replay", rowOfCurrent with { ExpiresAt = Now.AddSeconds(-1) }, older, null, RefreshDecision.Expired),
            ("expires now is expired", rowOfCurrent with { ExpiresAt = Now }, current, null, RefreshDecision.Expired),
        };

        foreach (var c in cases)
        {
            // Act
            var decision = RefreshRule.Classify(c.Row, c.Token, c.Key, Now);

            // Assert
            decision.ShouldBe(c.Expected, c.Name);
        }
    }

    [Test]
    public void a_delayed_copy_in_each_order_gives_the_same_pair_and_no_revoke()
    {
        // Arrange
        var token = RefreshToken.New();
        var key = Key();
        var row = new RefreshState(TokenHash.Of(token.Encode()).Hex, Now.AddDays(1), null);

        // Act
        var first = RefreshRule.Classify(row, token, key, Now);
        var pair = TokenDerivation.Derive(token, key, Now);
        var rotated = row with { RefreshHash = TokenHash.Of(pair.RefreshToken).Hex };
        var copyOne = RefreshRule.Classify(rotated, token, key, Now);
        var copyTwo = RefreshRule.Classify(rotated, token, key, Now.AddMinutes(1));
        var repeat = TokenDerivation.Derive(token, key, Now.AddMinutes(1));

        // Assert
        first.ShouldBe(RefreshDecision.Rotate);
        copyOne.ShouldBe(RefreshDecision.RotateRetry);
        copyTwo.ShouldBe(RefreshDecision.RotateRetry);
        repeat.RefreshToken.ShouldBe(pair.RefreshToken);
        repeat.AccessToken.ShouldBe(pair.AccessToken);
    }

    [Test]
    public void a_copy_that_lands_after_the_next_rotation_is_a_replay()
    {
        // Arrange
        var token = RefreshToken.New();
        var key = Key();
        RefreshToken.TryParse(TokenDerivation.Derive(token, key, Now).RefreshToken, out var next).ShouldBeTrue();
        var nextPair = TokenDerivation.Derive(next!, Key(), Now);
        var row = new RefreshState(TokenHash.Of(nextPair.RefreshToken).Hex, Now.AddDays(1), null);

        // Act
        var decision = RefreshRule.Classify(row, token, key, Now);

        // Assert
        decision.ShouldBe(RefreshDecision.Replay);
    }

    [Test]
    public void to_string_of_each_secret_type_hides_the_value()
    {
        // Arrange
        var refresh = RefreshToken.New();
        RetryKey.TryParse(Secrets.New(), out var key).ShouldBeTrue();
        var pair = TokenDerivation.Derive(refresh, key!.Value, Now);
        var hash = TokenHash.Of(refresh.Encode());
        var state = new RefreshState(hash.Hex, Now, null);

        // Act
        var printed = $"{refresh} {key} {pair} {hash} {state}";

        // Assert
        printed.ShouldNotContain(refresh.Secret);
        printed.ShouldNotContain(refresh.Family);
        printed.ShouldNotContain(key.Value.Value);
        printed.ShouldNotContain(pair.AccessToken);
        printed.ShouldNotContain(pair.RefreshToken);
        printed.ShouldNotContain(hash.Hex);
    }

    private static RetryKey Key()
    {
        RetryKey.TryParse(Secrets.New(), out var key).ShouldBeTrue();
        return key!.Value;
    }
}
