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
}
