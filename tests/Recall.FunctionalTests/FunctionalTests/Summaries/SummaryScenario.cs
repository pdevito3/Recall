namespace Recall.FunctionalTests.FunctionalTests.Summaries;

using System.Net;
using System.Net.Http.Json;
using BackWave.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.Summaries.Dtos;
using Recall.Server.Domain.Summaries.Jobs;
using Shouldly;
using TestUtilities;

/// <summary>A member of a tenant whose Admin connected the fake provider, and the routes and jobs of Summaries.</summary>
internal static class SummaryScenario
{
    /// <summary>A Monday in UTC, the zone of a new User.</summary>
    public static readonly DateOnly Monday = new(2026, 6, 1);

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    public static DateTimeOffset NoonOf(DateOnly day) => new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

    public static async Task<SessionDto> SessionOnAsync(this HttpClient client, DateOnly day, string words) => await client.SessionAtAsync(NoonOf(day), words);

    public static async Task<SessionDto> SessionAtAsync(this HttpClient client, DateTimeOffset startedAt, string words)
    {
        var session = await client.AddSessionAsync(startedAt);
        var saved = await client.SaveDraftAsync(session.Id, words, 0, startedAt.AddMinutes(1));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        return session;
    }

    public static async Task<SummaryDto> OpenAsync(this HttpClient client, SummaryPeriod period, DateOnly date)
    {
        var opened = await client.PostAsync(ApiRoutes.Summaries.Generate(period.ToString(), date), null);
        opened.StatusCode.ShouldBe(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        return (await opened.Content.ReadFromJsonAsync<SummaryDto>()).ShouldNotBeNull();
    }

    public static async Task<SummaryDto> ReadAsync(this HttpClient client, SummaryPeriod period, DateOnly date)
    {
        var read = await client.GetAsync(ApiRoutes.Summaries.Of(period.ToString(), date));
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        return (await read.Content.ReadFromJsonAsync<SummaryDto>()).ShouldNotBeNull();
    }

    /// <summary>Runs the job of a run that a request began, the way that a worker does.</summary>
    public static async Task RunAsync(string summaryId, int attempt = 1, Guid? jobId = null)
    {
        var row = await RowAsync(summaryId);
        var jobs = ActivatorUtilities.CreateInstance<SummaryJobs>(Services);
        await jobs.RunSummaryAsync(row.Id, row.UserId, row.TenantId, row.RunId!, new JobContext { JobId = jobId ?? Guid.NewGuid(), Attempt = attempt }, CancellationToken.None);
    }

    /// <summary>Opens the period and runs its job, as a person who opens a page and waits.</summary>
    public static async Task<SummaryDto> OpenAndRunAsync(this HttpClient client, SummaryPeriod period, DateOnly date)
    {
        var opened = await client.OpenAsync(period, date);
        if (opened.Status == nameof(SummaryStatus.Generating))
            await RunAsync(opened.Id!);
        return await client.ReadAsync(period, date);
    }

    public static async Task RefreshAsync(string userId, string tenantId, Guid? jobId = null, int attempt = 1)
    {
        var jobs = ActivatorUtilities.CreateInstance<SummaryJobs>(Services);
        await jobs.RefreshSummariesAsync(userId, tenantId, new JobContext { JobId = jobId ?? Guid.NewGuid(), Attempt = attempt }, CancellationToken.None);
    }

    public static Task<Summary> RowAsync(string summaryId) => TestBase.ExecuteDbContextAsync(db => db.Summaries
        .IgnoreQueryFilters()
        .AsNoTracking()
        .FirstAsync(s => s.Id == summaryId));

    public static Task<List<Summary>> RowsOfAsync(string userId) => TestBase.ExecuteDbContextAsync(db => db.Summaries
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(s => s.UserId == userId)
        .ToListAsync());
}
