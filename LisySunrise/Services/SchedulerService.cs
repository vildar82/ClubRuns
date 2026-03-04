using LisySunrise.Data;

namespace LisySunrise.Services;

public sealed class SchedulerService(
    ILogger<SchedulerService> logger,
    AttendanceJobService attendanceJob,
    SqliteRepository repository) : BackgroundService
{
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lightweight in-process scheduler.
        // It checks every 30 seconds and runs only once for a given Friday date.
        // This is not a separate business process; it only triggers the same AttendanceJobService
        // while bot mode is alive.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
                if (now is {DayOfWeek: DayOfWeek.Friday, Hour: 12, Minute: <= 10})
                {
                    var runDate = now.ToString("yyyy-MM-dd");
                    if (!await repository.RunExistsAsync(runDate, stoppingToken))
                    {
                        logger.LogInformation("Starting scheduled attendance run for {RunDate}", runDate);
                        // Scheduled run and manual /run use the same job logic and matching rules.
                        await attendanceJob.RunAsync(now.Date, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Friday scheduler");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
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