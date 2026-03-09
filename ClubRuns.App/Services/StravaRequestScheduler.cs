using ClubRuns.App.Data;

namespace ClubRuns.App.Services;

public sealed class StravaRequestScheduler(SqliteRepository repository, ILogger<StravaRequestScheduler> logger)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int ReadLimitPerWindow = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> ExecuteReadAsync<T>(long? eventInstanceId, long? userId, string requestType, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var requestId = await repository.EnqueueStravaRequestAsync(eventInstanceId, userId, requestType, ct);

        try
        {
            await WaitForWindowAsync(ct);
            await repository.MarkStravaRequestProcessingAsync(requestId, ct);
            var result = await action(ct);
            await repository.MarkStravaRequestDoneAsync(requestId, ct);
            return result;
        }
        catch (Exception ex)
        {
            await repository.MarkStravaRequestFailedAsync(requestId, ex.Message, ct);
            throw;
        }
    }

    private async Task WaitForWindowAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan? delay = null;

            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var since = now - Window;
                var recentCount = await repository.CountRecentCompletedStravaRequestsAsync(since, ct);
                if (recentCount < ReadLimitPerWindow)
                {
                    return;
                }

                var oldest = await repository.GetOldestRecentStravaRequestAsync(since, ct);
                if (oldest is null)
                {
                    return;
                }

                delay = oldest.Value + Window - now;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
            }
            finally
            {
                _gate.Release();
            }

            logger.LogInformation("Strava read limit window is full. Waiting {Delay} before next read request.", delay);
            await Task.Delay(delay!.Value, ct);
        }
    }
}
