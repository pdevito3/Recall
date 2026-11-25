namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Exceptions;
using Shouldly;
using static ImportScenario;

public class GetJournalDaysQueryTests : TestBase
{
    [Test]
    [Property("Rule", "IMP-26")]
    public async Task each_journaling_day_of_the_range_with_a_session_comes_with_its_count()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var scope = ScopeOf(owner);
        (DateTimeOffset Start, string Zone)[] starts =
        [
            (new DateTimeOffset(2024, 3, 2, 5, 30, 0, TimeSpan.Zero), "America/Chicago"),
            (new DateTimeOffset(2024, 3, 2, 12, 0, 0, TimeSpan.Zero), "UTC"),
            (new DateTimeOffset(2024, 3, 2, 13, 0, 0, TimeSpan.Zero), "UTC"),
            (new DateTimeOffset(2024, 3, 5, 12, 0, 0, TimeSpan.Zero), "UTC"),
            (new DateTimeOffset(2024, 2, 29, 23, 30, 0, TimeSpan.Zero), "Asia/Tokyo"),
        ];
        foreach (var (start, zone) in starts)
            await scope.SendAsync(new AddSession.Command(new SessionForCreationDto { ClientSessionId = Guid.NewGuid().ToString(), StartedAt = start, TimeZone = zone }));

        // Act
        var days = await scope.SendAsync(new GetJournalDays.Query(new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 4)));

        // Assert
        days.ShouldBe([
            new JournalDayDto { Date = new DateOnly(2024, 3, 1), SessionCount = 2 },
            new JournalDayDto { Date = new DateOnly(2024, 3, 2), SessionCount = 2 },
        ]);
    }

    [Test]
    [Property("Rule", "IMP-26")]
    [Arguments("2024-01-01", "2025-01-01")]
    [Arguments("2024-03-02", "2024-03-01")]
    public async Task a_range_of_more_than_366_days_or_with_the_end_before_the_start_gets_422(string from, string to)
    {
        // Arrange
        var owner = await AddOwnerAsync();

        // Act
        var read = () => ScopeOf(owner).SendAsync(new GetJournalDays.Query(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)));

        // Assert
        var refused = await read.ShouldThrowAsync<ValidationException>();
        (refused.Property, refused.Message).ShouldBe(("to", GetJournalDays.RangeMessage));
    }

    [Test]
    [Property("Rule", "IMP-26")]
    public async Task a_range_of_366_days_is_allowed()
    {
        // Arrange
        var owner = await AddOwnerAsync();

        // Act
        var days = await ScopeOf(owner).SendAsync(new GetJournalDays.Query(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)));

        // Assert
        days.ShouldBeEmpty();
    }
}
