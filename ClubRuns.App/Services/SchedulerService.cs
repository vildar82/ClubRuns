using System.Globalization;
using ClubRuns.App.Config;
using ClubRuns.App.Data;
using Microsoft.Extensions.Options;

namespace ClubRuns.App.Services;

public sealed class SchedulerService(
    ILogger<SchedulerService> logger,
    AttendanceJobService attendanceJob,
    SqliteRepository repository,
    IOptions<AppOptions> options) : BackgroundService
{
    private readonly string _defaultTimeZoneId = options.Value.Schedule.TimeZoneId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueRuns = await GetDueRunsAsync(DateTimeOffset.UtcNow, stoppingToken);
                foreach (var dueRun in dueRuns)
                {
                    logger.LogInformation(
                        "Starting scheduled attendance check for club run {ClubRunId} on {RunDate} at {DueAtUtc}",
                        dueRun.ClubRun.Id,
                        dueRun.RunDate,
                        dueRun.DueAtUtc);

                    var runDate = DateOnly.ParseExact(dueRun.RunDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    await attendanceJob.RunAsync(runDate.ToDateTime(TimeOnly.MinValue), dueRun.ClubRun.Id, null, skipExistingReports: true, stoppingToken);
                }

                var nextDueAtUtc = await GetNextDueAtUtcAsync(DateTimeOffset.UtcNow, stoppingToken);
                var delay = nextDueAtUtc - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.FromMinutes(1))
                {
                    delay = TimeSpan.FromMinutes(1);
                }

                logger.LogInformation("Next scheduled check at {NextDueAtUtc}", nextDueAtUtc);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task<List<DueRun>> GetDueRunsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var runs = await repository.GetClubRunsAsync(activeOnly: true, ct: ct);
        var dueRuns = new List<DueRun>();

        foreach (var clubRun in runs)
        {
            var club = await repository.GetClubByIdAsync(clubRun.ClubId, ct);
            if (club is null || !club.IsActive)
            {
                continue;
            }

            var timeZone = ResolveTimeZone(string.IsNullOrWhiteSpace(club.TimeZoneId) ? _defaultTimeZoneId : club.TimeZoneId);
            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
            var targetDate = DateOnly.FromDateTime(nowLocal.DateTime);
            if ((int)targetDate.DayOfWeek != clubRun.DayOfWeek)
            {
                continue;
            }

            var dueAtUtc = BuildDueAtUtc(targetDate, clubRun.CheckAtLocal, timeZone);
            if (dueAtUtc > nowUtc)
            {
                continue;
            }

            var eventInstance = await repository.GetEventInstanceAsync(clubRun.Id, targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
            if (eventInstance is not null && await repository.EventReportExistsAsync(eventInstance.Id, ct))
            {
                continue;
            }

            dueRuns.Add(new DueRun(clubRun, targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dueAtUtc));
        }

        return dueRuns.OrderBy(x => x.DueAtUtc).ToList();
    }

    private async Task<DateTimeOffset> GetNextDueAtUtcAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var runs = await repository.GetClubRunsAsync(activeOnly: true, ct: ct);
        DateTimeOffset? nextDueAtUtc = null;

        foreach (var clubRun in runs)
        {
            var club = await repository.GetClubByIdAsync(clubRun.ClubId, ct);
            if (club is null || !club.IsActive)
            {
                continue;
            }

            var timeZone = ResolveTimeZone(string.IsNullOrWhiteSpace(club.TimeZoneId) ? _defaultTimeZoneId : club.TimeZoneId);
            var candidate = GetNextDueAtUtc(clubRun, nowUtc, timeZone);
            if (!nextDueAtUtc.HasValue || candidate < nextDueAtUtc.Value)
            {
                nextDueAtUtc = candidate;
            }
        }

        return nextDueAtUtc ?? nowUtc.AddHours(1);
    }

    private static DateTimeOffset GetNextDueAtUtc(ClubRunRecord clubRun, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var currentDate = DateOnly.FromDateTime(nowLocal.DateTime);
        var dayDiff = ((clubRun.DayOfWeek - (int)currentDate.DayOfWeek) + 7) % 7;
        var targetDate = currentDate.AddDays(dayDiff);
        var candidate = BuildDueAtUtc(targetDate, clubRun.CheckAtLocal, timeZone);

        if (candidate <= nowUtc)
        {
            targetDate = targetDate.AddDays(7);
            candidate = BuildDueAtUtc(targetDate, clubRun.CheckAtLocal, timeZone);
        }

        return candidate;
    }

    private static DateTimeOffset BuildDueAtUtc(DateOnly date, string checkAtLocal, TimeZoneInfo timeZone)
    {
        var localTime = TimeSpan.ParseExact(checkAtLocal, @"hh\:mm", CultureInfo.InvariantCulture);
        var localDateTime = date.ToDateTime(TimeOnly.MinValue).Add(localTime);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            foreach (var id in new[] { "Asia/Tbilisi", "Georgian Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch
                {
                }
            }

            throw new InvalidOperationException($"Cannot resolve timezone '{timeZoneId}'.");
        }
    }

    private sealed record DueRun(ClubRunRecord ClubRun, string RunDate, DateTimeOffset DueAtUtc);
}
