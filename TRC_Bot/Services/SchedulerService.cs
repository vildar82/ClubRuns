using Microsoft.Extensions.Options;

namespace TRC_Bot;

public sealed class SchedulerService(
    ILogger<SchedulerService> logger,
    AttendanceJobService attendanceJob,
    SqliteRepository repository,
    IOptions<AppOptions> options) : BackgroundService
{
    private readonly AppOptions _options = options.Value;
    // All schedule calculations are performed in Tbilisi local time.
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In-process scheduler with "sleep until next planned run" behavior.
        // This avoids frequent polling loops and wakes up only near schedule time.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Convert from UTC clock to business-local time.
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
                var schedule = _options.Schedule;
                ValidateSchedule(schedule);

                // Catch-up scenario: app started/restarted during schedule window.
                if (IsInsideScheduleWindow(now, schedule))
                    await TryRunForDateAsync(DateOnly.FromDateTime(now.DateTime), stoppingToken);

                // Compute next planned schedule point and sleep until it.
                var nextLocal = GetNextWindowStart(now, schedule);
                var delay = nextLocal - now;
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                logger.LogInformation("Next scheduled check at {NextLocal}", nextLocal);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown path.
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler loop");
                // In case of transient failures, wait briefly before recomputing next run.
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task TryRunForDateAsync(DateOnly localDate, CancellationToken ct)
    {
        var runDate = localDate.ToString("yyyy-MM-dd");
        // Idempotency guard: do not run scheduled job twice for same date.
        if (await repository.RunExistsAsync(runDate, ct))
        {
            logger.LogInformation("Scheduled run already exists for {RunDate}. Skipping.", runDate);
            return;
        }

        logger.LogInformation("Starting scheduled attendance run for {RunDate}", runDate);
        // Scheduled run and manual /run use the same job logic and matching rules.
        await attendanceJob.RunAsync(localDate.ToDateTime(TimeOnly.MinValue), ct);
    }

    private static bool IsInsideScheduleWindow(DateTimeOffset now, ScheduleOptions schedule)
    {
        // Schedule window applies only on configured day + hour.
        if (now.DayOfWeek != schedule.DayOfWeek || now.Hour != schedule.Hour)
            return false;

        return now.Minute >= schedule.MinuteFrom && now.Minute <= schedule.MinuteTo;
    }

    private static DateTimeOffset GetNextWindowStart(DateTimeOffset now, ScheduleOptions schedule)
    {
        // Find nearest future occurrence for (DayOfWeek, Hour, MinuteFrom).
        var localNow = now.DateTime;
        var dayDiff = ((int)schedule.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7;
        var candidateDate = localNow.Date.AddDays(dayDiff);
        var candidateLocal = candidateDate
            .AddHours(schedule.Hour)
            .AddMinutes(schedule.MinuteFrom);
        var candidateOffset = new DateTimeOffset(candidateLocal, now.Offset);

        // If this week's slot already passed, move to next week.
        if (candidateOffset <= now)
        {
            candidateLocal = candidateLocal.AddDays(7);
            candidateOffset = new DateTimeOffset(candidateLocal, now.Offset);
        }

        return candidateOffset;
    }

    private static void ValidateSchedule(ScheduleOptions schedule)
    {
        // Fail fast on invalid config to avoid silent scheduling mistakes.
        if (schedule.Hour is < 0 or > 23)
            throw new InvalidOperationException("Schedule.Hour must be in range 0..23.");

        if (schedule.MinuteFrom is < 0 or > 59 || schedule.MinuteTo is < 0 or > 59)
            throw new InvalidOperationException("Schedule.MinuteFrom and Schedule.MinuteTo must be in range 0..59.");

        if (schedule.MinuteFrom > schedule.MinuteTo)
            throw new InvalidOperationException("Schedule.MinuteFrom must be less than or equal to Schedule.MinuteTo.");
    }

    private static TimeZoneInfo ResolveTbilisiTimeZone()
    {
        // Support both Linux and Windows timezone identifiers.
        foreach (var id in new[] { "Asia/Tbilisi", "Georgian Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // Try next ID.
            }
        }

        throw new InvalidOperationException("Cannot resolve timezone for Asia/Tbilisi.");
    }
}
