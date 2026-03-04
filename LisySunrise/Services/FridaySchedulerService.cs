using LisySunrise.Data;

namespace LisySunrise.Services;

public sealed class FridaySchedulerService(
    ILogger<FridaySchedulerService> logger,
    AttendanceJobService attendanceJob,
    SqliteRepository repository) : BackgroundService
{
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lightweight in-process scheduler.
        // It checks every 30 seconds and runs only once for a given Friday date.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
                if (now.DayOfWeek == DayOfWeek.Friday && now.Hour == 12 && now.Minute <= 10)
                {
                    var runDate = now.ToString("yyyy-MM-dd");
                    if (!await repository.RunExistsAsync(runDate, stoppingToken))
                    {
                        logger.LogInformation("Starting scheduled attendance run for {RunDate}", runDate);
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
