using System.Text.RegularExpressions;

namespace LisiSunrise;

public sealed class LegacyStatsImporterService(SqliteRepository repository, ILogger<LegacyStatsImporterService> logger)
{
    private static readonly Regex UsernameRegex = new(@"https?://t\.me/([A-Za-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b(\d{1,4})\b", RegexOptions.Compiled);
    private static readonly Regex LinkInParenthesesRegex = new(@"\(\s*https?://[^)]+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<(int imported, int skipped)> ImportAsync(string rawText, CancellationToken ct = default)
    {
        var lines = rawText
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var parsed = new Dictionary<string, LegacyStatUpsert>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var line in lines)
        {
            if (IsSummaryLine(line))
                continue;

            var item = TryParseLine(line);
            if (item is null)
            {
                skipped++;
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(item.TelegramUsername)
                ? $"u:{item.TelegramUsername!.ToLowerInvariant()}"
                : $"n:{item.DisplayName.ToLowerInvariant()}";

            // If duplicates exist, keep the richer record.
            if (parsed.TryGetValue(key, out var existing))
            {
                if (item.RunsCount > existing.RunsCount)
                    parsed[key] = item;
            }
            else
            {
                parsed[key] = item;
            }
        }

        await repository.ReplaceLegacyStatsAsync(parsed.Values.ToList(), ct);
        logger.LogInformation("Legacy stats imported. Imported={Imported}, Skipped={Skipped}", parsed.Count, skipped);
        return (parsed.Count, skipped);
    }

    private static LegacyStatUpsert? TryParseLine(string line)
    {
        var numberMatch = NumberRegex.Match(line);
        var sunCount = CountEmoji(line, "🌞");
        var noSunCount = CountEmoji(line, "🌥");

        var runsCount = 0;
        if (numberMatch.Success)
            runsCount = int.Parse(numberMatch.Groups[1].Value);
        else if (sunCount + noSunCount > 0)
            runsCount = sunCount + noSunCount;

        if (runsCount <= 0)
            return null;

        var displayName = ExtractDisplayName(line, numberMatch);
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var username = ExtractTelegramUsername(line);
        return new LegacyStatUpsert(displayName, username, runsCount, sunCount, noSunCount, line);
    }

    private static string? ExtractTelegramUsername(string line)
    {
        var match = UsernameRegex.Match(line);
        if (!match.Success)
            return null;

        return match.Groups[1].Value;
    }

    private static string ExtractDisplayName(string line, Match numberMatch)
    {
        var head = numberMatch.Success ? line[..numberMatch.Index] : line;
        head = LinkInParenthesesRegex.Replace(head, "");
        head = head.Replace("🏃", "").Replace("😎", "").Replace("🌞", "").Replace("🌥", "");
        head = Regex.Replace(head, @"\s+", " ").Trim();
        return head.Trim('-', ' ', '\t');
    }

    private static int CountEmoji(string text, string emoji)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(emoji, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += emoji.Length;
        }

        return count;
    }

    private static bool IsSummaryLine(string line)
    {
        if (line.StartsWith("__", StringComparison.Ordinal))
            return true;

        var normalized = line.Trim();
        return normalized.StartsWith("Total ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Latest run attendance", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Highest attendance", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Leaderboard", StringComparison.OrdinalIgnoreCase);
    }
}