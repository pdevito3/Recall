namespace Recall.UnitTests.Auth;

using Recall.Server.Auth;
using Shouldly;

public class OwnerClaimTests
{
    [Test]
    public async Task one_of_many_callers_at_the_same_time_wins_a_file_and_leaves_no_other_entry()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_claim_").FullName;
        var file = Path.Combine(folder, OwnerClaim.FileName);
        var underTest = new OwnerClaim(file, TimeProvider.System);
        try
        {
            for (var round = 0; round < 5000; round++)
            {
                await File.WriteAllTextAsync(file, "");
                using var start = new ManualResetEventSlim();
                var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    return underTest.TryConsume();
                })).ToList();

                // Act
                start.Set();
                var wins = await Task.WhenAll(callers);

                // Assert
                wins.Count(won => won).ShouldBe(1, $"round {round}");
                Directory.GetFileSystemEntries(folder).ShouldBeEmpty();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task only_a_regular_file_within_the_age_limit_is_fresh_and_the_check_leaves_each_entry_in_place()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_claim_").FullName;
        var file = Path.Combine(folder, OwnerClaim.FileName);
        var underTest = new OwnerClaim(file, TimeProvider.System);
        try
        {
            var missing = underTest.IsFresh();
            await File.WriteAllTextAsync(file, "");
            var fresh = underTest.IsFresh();
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - OwnerClaim.MaxAge - TimeSpan.FromMinutes(1));
            var old = underTest.IsFresh();
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow + OwnerClaim.MaxAge + TimeSpan.FromMinutes(1));
            var future = underTest.IsFresh();
            File.Delete(file);
            var target = Path.Combine(folder, "target");
            await File.WriteAllTextAsync(target, "");
            File.CreateSymbolicLink(file, target);
            var link = underTest.IsFresh();
            File.Delete(file);
            Directory.CreateDirectory(file);

            // Act
            var directory = underTest.IsFresh();

            // Assert
            missing.ShouldBeFalse();
            fresh.ShouldBeTrue();
            old.ShouldBeFalse();
            future.ShouldBeFalse();
            link.ShouldBeFalse();
            directory.ShouldBeFalse();
            Directory.Exists(file).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
