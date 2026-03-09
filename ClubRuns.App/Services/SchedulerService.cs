using ClubRuns.App.Config;
using Microsoft.Extensions.Options;

namespace ClubRuns.App.Services;

public sealed class SchedulerService(
    ILogger<SchedulerService> logger,
    AttendanceJobService attendanceJob,
    IOptions<AppOptions> options) : BackgroundService
{
    private readonly AppOptions _options = options.Value;
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
                var schedule = _options.Schedule;
                ValidateSchedule(schedule);

                if (IsInsideScheduleWindow(now, schedule))
                {
                    await TryRunForDateAsync(DateOnly.FromDateTime(now.DateTime), stoppingToken);
                }

                var nextLocal = GetNextWindowStart(now, schedule);
                var delay = nextLocal - now;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                logger.LogInformation("Next scheduled check at {NextLocal}", nextLocal);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler loop");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task TryRunForDateAsync(DateOnly localDate, CancellationToken ct)
    {
        logger.LogInformation("Starting scheduled club run check for {RunDate}", localDate);
        await attendanceJob.RunAsync(localDate.ToDateTime(TimeOnly.MinValue), null, null, skipExistingReports: true, ct);
    }

    private static bool IsInsideScheduleWindow(DateTimeOffset now, ScheduleOptions schedule)
    {
        if (now.DayOfWeek != schedule.DayOfWeek || now.Hour != schedule.Hour)
        {
            return false;
        }

        return now.Minute >= schedule.MinuteFrom && now.Minute <= schedule.MinuteTo;
    }

    private static DateTimeOffset GetNextWindowStart(DateTimeOffset now, ScheduleOptions schedule)
    {
        var localNow = now.DateTime;
        var dayDiff = ((int)schedule.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7;
        var candidateDate = localNow.Date.AddDays(dayDiff);
        var candidateLocal = candidateDate.AddHours(schedule.Hour).AddMinutes(schedule.MinuteFrom);
        var candidateOffset = new DateTimeOffset(candidateLocal, now.Offset);

        if (candidateOffset <= now)
        {
            candidateOffset = candidateOffset.AddDays(7);
        }

        return candidateOffset;
    }

    private static void ValidateSchedule(ScheduleOptions schedule)
    {
        if (schedule.Hour is < 0 or > 23)
        {
            throw new InvalidOperationException("Schedule.Hour must be in range 0..23.");
        }

        if (schedule.MinuteFrom is < 0 or > 59 || schedule.MinuteTo is < 0 or > 59)
        {
            throw new InvalidOperationException("Schedule.MinuteFrom and Schedule.MinuteTo must be in range 0..59.");
        }

        if (schedule.MinuteFrom > schedule.MinuteTo)
        {
            throw new InvalidOperationException("Schedule.MinuteFrom must be less than or equal to Schedule.MinuteTo.");
        }
    }

    private static TimeZoneInfo ResolveTbilisiTimeZone()
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

        throw new InvalidOperationException("Cannot resolve timezone for Asia/Tbilisi.");
    }
}
