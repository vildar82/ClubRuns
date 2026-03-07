using System.Globalization;

namespace TRC_Bot;

public sealed class LeaderboardService(SqliteRepository repository)
{
    public async Task<LeaderboardSummary> BuildSummaryAsync(CancellationToken ct = default)
    {
        var legacy = await repository.GetLegacyStatsAsync(ct);
        var auto = await repository.GetAutoAttendanceAggregatesAsync(ct);
        var (totalRuns, latestAttendance, highestAttendance, highestDate) = await repository.GetRunsSummaryAsync(ct);

        var map = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in legacy)
        {
            var key = BuildKey(item.TelegramUsername, item.DisplayName);
            map[key] = new LeaderboardEntry(
                item.DisplayName,
                item.TelegramUsername,
                item.RunsCount,
                item.SunCount,
                item.NoSunCount);
        }

        foreach (var item in auto)
        {
            var displayName = BuildDisplayName(item.TelegramUsername, item.FirstName, item.LastName, item.TelegramUserId);
            var key = BuildKey(item.TelegramUsername, displayName);
            var runs = item.SunCount + item.NoSunCount;

            if (map.TryGetValue(key, out var existing))
            {
                map[key] = existing with
                {
                    RunsCount = existing.RunsCount + runs,
                    SunCount = existing.SunCount + item.SunCount,
                    NoSunCount = existing.NoSunCount + item.NoSunCount,
                    TelegramUsername = existing.TelegramUsername ?? item.TelegramUsername
                };
            }
            else
            {
                map[key] = new LeaderboardEntry(
                    displayName,
                    item.TelegramUsername,
                    runs,
                    item.SunCount,
                    item.NoSunCount);
            }
        }

        var entries = map.Values
            .Where(x => x.RunsCount > 0)
            .OrderByDescending(x => x.RunsCount)
            .ThenByDescending(x => x.SunCount)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LeaderboardSummary(
            entries,
            TotalParticipants: entries.Count,
            TotalFoundAttendances: entries.Sum(x => x.SunCount),
            TotalMissedAttendances: entries.Sum(x => x.NoSunCount),
            TotalTrackedAttempts: entries.Sum(x => x.RunsCount),
            AutoRunsCount: totalRuns,
            LatestRunAttendance: latestAttendance,
            HighestAttendance: highestAttendance,
            HighestAttendanceDate: highestDate);
    }

    public string BuildText(LeaderboardSummary summary, int maxItems = 82)
    {
        var lines = new List<string>
        {
            "TRC Attendance Leaderboard",
            string.Empty
        };

        var rank = 1;
        foreach (var item in summary.Entries.Take(maxItems))
        {
            var name = item.TelegramUsername is { Length: > 0 }
                ? $"{item.DisplayName} (@{item.TelegramUsername})"
                : item.DisplayName;

            lines.Add($"{rank}. {name} - {item.RunsCount} (found {item.SunCount} / missed {item.NoSunCount})");
            rank++;
        }

        lines.Add(string.Empty);
        lines.Add($"Total participants: {summary.TotalParticipants}");
        lines.Add($"Total found attendances: {summary.TotalFoundAttendances}");
        lines.Add($"Total missed attendances: {summary.TotalMissedAttendances}");
        lines.Add($"Total tracked attempts: {summary.TotalTrackedAttempts}");
        lines.Add($"Auto-tracked reports: {summary.AutoRunsCount}");

        if (summary.LatestRunAttendance.HasValue)
        {
            lines.Add($"Latest attendance: {summary.LatestRunAttendance.Value}");
        }

        if (summary.HighestAttendance.HasValue)
        {
            var date = summary.HighestAttendanceDate is { Length: > 0 }
                ? summary.HighestAttendanceDate
                : "n/a";
            lines.Add($"Highest attendance (auto-tracked): {summary.HighestAttendance.Value} ({date})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildKey(string? username, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            return $"u:{username.Trim().ToLowerInvariant()}";
        }

        return $"n:{displayName.Trim().ToLowerInvariant()}";
    }

    private static string BuildDisplayName(string? username, string? firstName, string? lastName, long telegramUserId)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username;
        }

        var fullName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName)
            ? telegramUserId.ToString(CultureInfo.InvariantCulture)
            : fullName;
    }
}
