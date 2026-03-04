using System.Globalization;
using LisySunrise.Config;
using LisySunrise.Data;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace LisySunrise.Services;

public sealed class AttendanceJobService(
    SqliteRepository repository,
    StravaApiClient stravaApi,
    ITelegramBotClient telegramBot,
    ILogger<AttendanceJobService> logger,
    IOptions<AppOptions> options)
{
    private readonly AppOptions _options = options.Value;
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    // Default execution path used in bot mode and /run command:
    // run job + publish to configured default chat (if provided).
    public Task<AttendanceRunResult> RunAsync(DateTime? targetLocalDate = null, CancellationToken ct = default) =>
        RunAsync(targetLocalDate, publishToDefaultTarget: true, ct);

    // Explicit execution path used by console "job" mode:
    // run job only, then caller decides where to print/send report.
    public async Task<AttendanceRunResult> RunAsync(DateTime? targetLocalDate, bool publishToDefaultTarget, CancellationToken ct = default)
    {
        // Build the run date in Tbilisi local time because attendance is local-event based.
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
        var runDate = (targetLocalDate ?? nowLocal.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var users = await repository.GetAllUsersWithAuthAsync(ct);
        var found = new List<AttendanceUserResult>();
        var notFound = new List<AttendanceUserResult>();
        var errors = new List<AttendanceUserResult>();

        var windowStart = ParseLocalTime(_options.Matching.WindowStartLocal);
        var windowEnd = ParseLocalTime(_options.Matching.WindowEndLocal);
        var targetStart = ParseLocalTime(_options.Matching.TargetStartLocal);
        // Strava activities API filter uses UNIX timestamps.
        var after = ToUnixInTbilisi(DateTime.ParseExact(runDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).Add(windowStart));
        var before = ToUnixInTbilisi(DateTime.ParseExact(runDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).Add(windowEnd));

        // Process users independently: one broken token must not fail the whole report.
        foreach (var userWithAuth in users)
        {
            var user = userWithAuth.User;

            if (userWithAuth.Auth is null)
            {
                // User exists in Telegram but has not connected Strava yet.
                var item = new AttendanceUserResult(user, false, null, "No Strava connection", false, null);
                notFound.Add(item);
                await repository.UpsertAttendanceAsync(new AttendanceUpsert(runDate, user.Id, null, null, null, null, null, item.Reason), ct);
                continue;
            }

            try
            {
                var auth = userWithAuth.Auth;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= auth.ExpiresAt - 60)
                {
                    // Strava rotates refresh tokens; always store both new access and refresh tokens.
                    var refreshed = await stravaApi.RefreshTokenAsync(auth.RefreshToken, ct);
                    await repository.SaveStravaAuthAsync(
                        user.Id,
                        refreshed.Athlete.Id,
                        refreshed.AccessToken,
                        refreshed.RefreshToken,
                        refreshed.ExpiresAt,
                        refreshed.Scope,
                        ct);
                    auth = new StravaAuthRecord(user.Id, refreshed.Athlete.Id, refreshed.AccessToken, refreshed.RefreshToken, refreshed.ExpiresAt, refreshed.Scope, DateTimeOffset.UtcNow);
                }

                var activities = await stravaApi.GetActivitiesAsync(auth.AccessToken, after, before, ct);
                var best = FindBestMatch(activities, windowStart, windowEnd, targetStart);

                if (best is null)
                {
                    // No activity passed filters (time/radius/type/distance).
                    var item = new AttendanceUserResult(user, false, null, "No matching activity in configured window/radius", false, null);
                    notFound.Add(item);
                    await repository.UpsertAttendanceAsync(new AttendanceUpsert(runDate, user.Id, null, null, null, null, null, item.Reason), ct);
                    continue;
                }

                var startLocal = ParseStravaLocal(best.StartDateLocal);
                var lat = best.StartLatLng![0];
                var lng = best.StartLatLng![1];
                var distanceKm = best.DistanceMeters / 1000.0;

                var foundItem = new AttendanceUserResult(
                    user,
                    true,
                    best.Id,
                    $"type={best.Type}, distance={distanceKm:F2}km, radius<= {_options.Matching.RadiusKm:F1}km",
                    false,
                    null);
                found.Add(foundItem);

                await repository.UpsertAttendanceAsync(new AttendanceUpsert(
                    runDate,
                    user.Id,
                    best.Id,
                    new DateTimeOffset(startLocal, TbilisiTimeZone.GetUtcOffset(startLocal)),
                    lat,
                    lng,
                    distanceKm,
                    foundItem.Reason), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing user {TelegramUserId}", user.TelegramUserId);
                var item = new AttendanceUserResult(user, false, null, "User processing error", true, ex.Message);
                errors.Add(item);
                await repository.UpsertAttendanceAsync(new AttendanceUpsert(runDate, user.Id, null, null, null, null, null, $"error: {ex.Message}"), ct);
            }
        }

        await repository.SaveRunAsync(runDate, users.Count, found.Count, ct);
        // Report is published after DB write so audit history is always present.
        var result = new AttendanceRunResult(runDate, found, notFound, errors);
        if (publishToDefaultTarget)
        {
            await PublishReportAsync(result, ct);
        }

        return result;
    }

    private ActivityResponse? FindBestMatch(List<ActivityResponse> activities, TimeSpan windowStart, TimeSpan windowEnd, TimeSpan targetStart)
    {
        var allowedTypes = new HashSet<string>(_options.Matching.AllowedActivityTypes, StringComparer.OrdinalIgnoreCase);

        // Matching policy for MVP:
        // 1) filter by type/time/radius/distance
        // 2) pick the one closest to configured target start time
        var candidates = activities
            .Where(a => allowedTypes.Contains(a.Type))
            .Where(a => a.StartLatLng is { Count: >= 2 })
            .Select(a => new { Activity = a, LocalStart = ParseStravaLocal(a.StartDateLocal) })
            .Where(x => x.LocalStart.TimeOfDay >= windowStart && x.LocalStart.TimeOfDay <= windowEnd)
            .Where(x => HaversineKm(x.Activity.StartLatLng![0], x.Activity.StartLatLng[1], _options.Matching.LisiStartLat, _options.Matching.LisiStartLng) <= _options.Matching.RadiusKm)
            .Where(x => !_options.Matching.MinDistanceKm.HasValue || x.Activity.DistanceMeters / 1000.0 >= _options.Matching.MinDistanceKm.Value)
            .Where(x => !_options.Matching.MaxDistanceKm.HasValue || x.Activity.DistanceMeters / 1000.0 <= _options.Matching.MaxDistanceKm.Value)
            .OrderBy(x => Math.Abs((x.LocalStart.TimeOfDay - targetStart).TotalMinutes))
            .ThenBy(x => x.LocalStart)
            .Select(x => x.Activity)
            .ToList();

        return candidates.FirstOrDefault();
    }

    public async Task PublishReportAsync(AttendanceRunResult result, CancellationToken ct)
    {
        // If group chat id is not configured, reporting is skipped in automatic mode.
        if (!_options.Telegram.GroupChatId.HasValue)
        {
            logger.LogInformation("Telegram.GroupChatId is not configured. Skipping Telegram report publishing.");
            return;
        }

        await PublishReportToChatAsync(result, _options.Telegram.GroupChatId.Value, ct);
    }

    public async Task PublishReportToChatAsync(AttendanceRunResult result, long chatId, CancellationToken ct)
    {
        await telegramBot.SendMessage(
            chatId,
            BuildReportText(result),
            cancellationToken: ct);
    }

    public string BuildReportText(AttendanceRunResult result)
    {
        // Keep message plain-text and compact for Telegram readability.
        var lines = new List<string>
        {
            $"Lisi Sunrise attendance - {result.RunDate}",
            $"Found: {result.Found.Count} / {result.Found.Count + result.NotFound.Count + result.Errors.Count}",
            string.Empty,
            "✅ Found:"
        };

        lines.AddRange(result.Found.Count == 0
            ? ["- none"]
            : result.Found.Select(x => $"- {DisplayName(x.User)}: https://www.strava.com/activities/{x.ActivityId}"));

        lines.Add(string.Empty);
        lines.Add("❓ Not found:");
        lines.AddRange(result.NotFound.Count == 0
            ? ["- none"]
            : result.NotFound.Select(x => $"- {DisplayName(x.User)}"));

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ errors:");
            lines.AddRange(result.Errors.Select(x => $"- {DisplayName(x.User)} ({x.ErrorMessage})"));
        }

        lines.Add(string.Empty);
        lines.Add("If your run is private, bot may not see it. Reconnect with read_all or make activity visible.");
        return string.Join(Environment.NewLine, lines);
    }

    private static TimeSpan ParseLocalTime(string value) => TimeSpan.ParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture);

    private static long ToUnixInTbilisi(DateTime localTime)
    {
        // Convert local event time to unix seconds with explicit timezone offset.
        var offset = new DateTimeOffset(localTime, TbilisiTimeZone.GetUtcOffset(localTime));
        return offset.ToUnixTimeSeconds();
    }

    private static DateTime ParseStravaLocal(string value)
    {
        // Strava returns local datetime text; parse defensively for format variations.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt;
        }

        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        // Great-circle distance on Earth between activity start and Lisi target point.
        const double earthRadiusKm = 6371.0;
        var dLat = DegToRad(lat2 - lat1);
        var dLon = DegToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegToRad(lat1)) * Math.Cos(DegToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    private static string DisplayName(UserRecord user)
    {
        if (!string.IsNullOrWhiteSpace(user.TelegramUsername))
        {
            return $"@{user.TelegramUsername}";
        }

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? user.TelegramUserId.ToString(CultureInfo.InvariantCulture) : fullName;
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
