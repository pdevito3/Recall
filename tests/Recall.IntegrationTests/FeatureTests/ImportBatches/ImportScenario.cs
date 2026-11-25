namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using System.Globalization;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;

internal static class ImportScenario
{
    public static readonly DateOnly Day = new(2024, 3, 1);

    public static async Task<User> AddOwnerAsync()
    {
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner)
            .Under(tenant)
            .BuildAsync();
        return owner;
    }

    /// <summary>A web session of the owner: the one caller that accepts while the import review is on.</summary>
    public static TestingServiceScope ScopeOf(User owner, IServiceProvider? services = null)
    {
        var scope = services is null ? new TestingServiceScope() : new TestingServiceScope(services);
        scope.SetAppSession(owner.Identifier, SessionKind.Web);
        return scope;
    }

    public static Task<ImportBatchDto> OpenAsync(User owner, string zone = "UTC")
        => ScopeOf(owner).SendAsync(new OpenImportBatch.Command(new ImportBatchForCreationDto { Name = "Day One", TimeZone = zone }));

    public static ImportEntryPutDto Entry(string id, int position = 0, string raw = "words", DateOnly? date = null, string? time = null) => new()
    {
        ClientSessionId = id,
        Date = (date ?? Day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Position = position,
        Raw = raw,
        Time = time,
    };

    public static Task<ImportPutResultDto> PutAsync(User owner, string batchId, params ImportEntryPutDto[] entries)
        => PutAsync(ScopeOf(owner), batchId, entries);

    public static Task<ImportPutResultDto> PutAsync(TestingServiceScope scope, string batchId, params ImportEntryPutDto[] entries)
        => scope.SendAsync(new PutImportEntries.Command(batchId, new ImportEntriesPutDto { Entries = entries }));

    public static Task<ImportRemoveResultDto> RemoveAsync(User owner, string batchId, params string[] ids)
        => ScopeOf(owner).SendAsync(new RemoveImportEntries.Command(batchId, new ImportEntriesRemoveDto { ClientSessionIds = ids }));

    public static Task<ImportEntryPageDto> EntriesAsync(User owner, string batchId)
        => ScopeOf(owner).SendAsync(new GetImportEntries.Query(batchId, null, null));

    public static Task<ImportEntryReviewedDto> RejectAsync(User owner, string batchId, string id)
        => ScopeOf(owner).SendAsync(new ReviewImportEntry.Command(batchId, new ImportEntryReviewDto { ClientSessionId = id, Rejected = true }));

    public static Task<ImportDayReviewedDto> RejectDayAsync(User owner, string batchId, DateOnly date, bool rejected = true)
        => ScopeOf(owner).SendAsync(new ReviewImportDay.Command(batchId, date, new ImportDayReviewDto { Rejected = rejected }));

    public static IReadOnlyList<(string Id, string Outcome)> OutcomesOf(ImportPutResultDto result)
        => result.Outcomes.Select(o => (o.ClientSessionId, o.Outcome)).ToList();
}
