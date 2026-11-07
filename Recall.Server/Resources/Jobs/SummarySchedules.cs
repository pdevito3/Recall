namespace Recall.Server.Resources.Jobs;

using System.Collections.Concurrent;
using BackWave;
using BackWave.Core;
using BackWave.Storage;
using Domain.Summaries.Jobs;
using Domain.TimeZones;

/// <summary>
/// The nightly refresh of the Summaries of each User, at <see cref="NightlyHour"/> in the time zone of the User. A schedule
/// with no overlap skips a night while the refresh of the night before still runs. A schedule that is in place is not written
/// again, because a write moves its cursor to now and so drops a night that a stop missed.
/// </summary>
public sealed class SummarySchedules(BackWaveClient client, IJobStore store)
{
    public const string IdPrefix = "summaries-refresh-";

    /// <summary>After midnight, so the Day that ended is whole, and before a person wakes.</summary>
    public const int NightlyHour = 3;

    public static readonly CronExpression Nightly = Cron.Daily(NightlyHour);

    private readonly ConcurrentDictionary<string, string> _inPlace = new();

    public static string IdOf(string userId) => IdPrefix + userId;

    public async Task EnsureAsync(string userId, string tenantId, TimeZoneId zone, CancellationToken cancellationToken)
    {
        if (_inPlace.TryGetValue(userId, out var known) && known == zone.Value)
            return;

        var stored = (await store.ListSchedulesAsync(cancellationToken)).Select(s => s.Schedule).FirstOrDefault(s => s.ScheduleId == IdOf(userId));
        if (stored is not { NoOverlap: true } || stored.TimeZoneId != zone.Value || stored.Cron != Nightly.Canonical || stored.WireName != SummaryJobs.RefreshWireName)
        {
            await client.UpsertRecurringAsync(
                IdOf(userId), Nightly, new RefreshSummaries(userId, tenantId),
                timeZone: zone.Value, catchUp: CatchUpPolicy.Coalesce, noOverlap: true, cancellationToken: cancellationToken);
        }

        _inPlace[userId] = zone.Value;
    }

    /// <summary>Removes the schedule of each User that is not in <paramref name="liveUserIds"/>.</summary>
    public async Task RemoveAllButAsync(IReadOnlySet<string> liveUserIds, CancellationToken cancellationToken)
    {
        foreach (var schedule in (await store.ListSchedulesAsync(cancellationToken)).Select(s => s.Schedule))
        {
            if (!schedule.ScheduleId.StartsWith(IdPrefix, StringComparison.Ordinal) || liveUserIds.Contains(schedule.ScheduleId[IdPrefix.Length..]))
                continue;

            await client.RemoveRecurringAsync(schedule.ScheduleId, cancellationToken);
            _inPlace.TryRemove(schedule.ScheduleId[IdPrefix.Length..], out _);
        }
    }
}
