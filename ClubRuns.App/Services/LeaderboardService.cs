namespace ClubRuns.App.Services;

using ClubRuns.App.Data;

public sealed class LeaderboardService(SqliteRepository repository)
{
    public async Task<LeaderboardSummary> BuildSummaryAsync(CancellationToken ct = default)
    {
        var rows = await repository.GetRunStatisticsAsync(ct);
        var entries = rows
            .Where(x => x.EventsCount > 0 || x.TotalRegistered > 0 || x.TotalFound > 0 || x.TotalNotFound > 0 || x.TotalErrors > 0)
            .Select(x => new RunStatisticsEntry(
                x.ClubRunId,
                x.ClubName,
                x.RunName,
                x.EventsCount,
                x.TotalRegistered,
                x.TotalFound,
                x.TotalNotFound,
                x.TotalErrors,
                x.BestAttendance,
                x.BestAttendanceDate,
                x.LatestEventDate))
            .ToList();

        return new LeaderboardSummary(
            entries,
            TotalRuns: entries.Count,
            TotalEvents: entries.Sum(x => x.EventsCount),
            TotalRegistered: entries.Sum(x => x.TotalRegistered),
            TotalFound: entries.Sum(x => x.TotalFound),
            TotalNotFound: entries.Sum(x => x.TotalNotFound),
            TotalErrors: entries.Sum(x => x.TotalErrors));
    }

    public string BuildText(LeaderboardSummary summary, int maxItems = 20)
    {
        if (summary.Entries.Count == 0)
        {
            return string.Join(Environment.NewLine, [
                "ClubRuns Run Statistics",
                string.Empty,
                "No run statistics yet.",
                "Run attendance jobs first, then this command will show per-run totals."
            ]);
        }

        var lines = new List<string>
        {
            "ClubRuns Run Statistics",
            string.Empty
        };

        var rank = 1;
        foreach (var item in summary.Entries.Take(maxItems))
        {
            lines.Add($"{rank}. {item.ClubName} / {item.RunName}");
            lines.Add($"   Events: {item.EventsCount} | Found: {item.TotalFound} | Registered: {item.TotalRegistered} | Missed: {item.TotalNotFound} | Errors: {item.TotalErrors}");

            if (!string.IsNullOrWhiteSpace(item.LatestEventDate) || item.BestAttendance > 0)
            {
                var bestDate = string.IsNullOrWhiteSpace(item.BestAttendanceDate) ? "n/a" : item.BestAttendanceDate;
                var latestDate = string.IsNullOrWhiteSpace(item.LatestEventDate) ? "n/a" : item.LatestEventDate;
                lines.Add($"   Best attendance: {item.BestAttendance} ({bestDate}) | Latest event: {latestDate}");
            }

            rank++;
        }

        lines.Add(string.Empty);
        lines.Add($"Total runs: {summary.TotalRuns}");
        lines.Add($"Total events: {summary.TotalEvents}");
        lines.Add($"Total found: {summary.TotalFound}");
        lines.Add($"Total registered: {summary.TotalRegistered}");
        lines.Add($"Total missed: {summary.TotalNotFound}");
        lines.Add($"Total errors: {summary.TotalErrors}");
        return string.Join(Environment.NewLine, lines);
    }
}
