using System.Globalization;
using Telegram.Bot;

namespace TRC_Bot;

public sealed class AttendanceJobService(
    SqliteRepository repository,
    StravaApiClient stravaApi,
    ITelegramBotClient telegramBot,
    ILogger<AttendanceJobService> logger)
{
    private static readonly TimeZoneInfo TbilisiTimeZone = ResolveTbilisiTimeZone();

    public async Task<List<ClubRunAttendanceResult>> RunAsync(
        DateTime? targetLocalDate = null,
        long? specificClubRunId = null,
        long? publishChatId = null,
        bool skipExistingReports = false,
        CancellationToken ct = default)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TbilisiTimeZone);
        var targetDate = DateOnly.FromDateTime((targetLocalDate ?? nowLocal.Date).Date);

        List<ClubRunRecord> runs;
        if (specificClubRunId.HasValue)
        {
            var clubRun = await repository.GetClubRunByIdAsync(specificClubRunId.Value, ct);
            runs = clubRun is null ? [] : [clubRun];
        }
        else
        {
            runs = await repository.GetClubRunsForDayAsync(targetDate.DayOfWeek, activeOnly: true, ct);
        }

        var results = new List<ClubRunAttendanceResult>();
        foreach (var clubRun in runs)
        {
            var result = await RunClubRunAsync(clubRun, targetDate, publishChatId, skipExistingReports, ct);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private async Task<ClubRunAttendanceResult?> RunClubRunAsync(
        ClubRunRecord clubRun,
        DateOnly targetDate,
        long? publishChatId,
        bool skipExistingReports,
        CancellationToken ct)
    {
        var runDate = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (skipExistingReports && await repository.ClubRunReportExistsAsync(clubRun.Id, runDate, ct))
        {
            logger.LogInformation("Club run report already exists for {ClubRunId} {RunDate}", clubRun.Id, runDate);
            return null;
        }

        var users = await repository.GetClubRunMembersWithAuthAsync(clubRun.Id, ct);
        var found = new List<AttendanceUserResult>();
        var notFound = new List<AttendanceUserResult>();
        var errors = new List<AttendanceUserResult>();

        var windowStart = ParseLocalTime(clubRun.WindowStartLocal);
        var windowEnd = ParseLocalTime(clubRun.WindowEndLocal);
        var targetStart = ParseLocalTime(clubRun.TargetStartLocal);
        var after = ToUnixInTbilisi(targetDate.ToDateTime(TimeOnly.MinValue).Add(windowStart));
        var before = ToUnixInTbilisi(targetDate.ToDateTime(TimeOnly.MinValue).Add(windowEnd));

        foreach (var userWithAuth in users)
        {
            var user = userWithAuth.User;
            if (userWithAuth.Auth is null)
            {
                var item = new AttendanceUserResult(user, false, null, "No Strava connection", false, null);
                notFound.Add(item);
                await repository.UpsertClubRunAttendanceAsync(
                    new ClubRunAttendanceUpsert(clubRun.Id, runDate, user.Id, null, null, null, null, null, item.Reason),
                    ct);
                continue;
            }

            try
            {
                var auth = userWithAuth.Auth;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= auth.ExpiresAt - 60)
                {
                    var refreshed = await stravaApi.RefreshTokenAsync(auth.RefreshToken, ct);
                    await repository.SaveStravaAuthAsync(
                        user.Id,
                        refreshed.Athlete.Id,
                        refreshed.AccessToken,
                        refreshed.RefreshToken,
                        refreshed.ExpiresAt,
                        refreshed.Scope,
                        ct);
                    auth = new StravaAuthRecord(
                        user.Id,
                        refreshed.Athlete.Id,
                        refreshed.AccessToken,
                        refreshed.RefreshToken,
                        refreshed.ExpiresAt,
                        refreshed.Scope,
                        DateTimeOffset.UtcNow);
                }

                var activities = await stravaApi.GetActivitiesAsync(auth.AccessToken, after, before, ct);
                var best = FindBestMatch(clubRun, activities, windowStart, windowEnd, targetStart);
                if (best is null)
                {
                    var item = new AttendanceUserResult(user, false, null, "No matching activity", false, null);
                    notFound.Add(item);
                    await repository.UpsertClubRunAttendanceAsync(
                        new ClubRunAttendanceUpsert(clubRun.Id, runDate, user.Id, null, null, null, null, null, item.Reason),
                        ct);
                    continue;
                }

                var startLocal = ParseStravaLocal(best.StartDateLocal);
                var lat = best.StartLatLng![0];
                var lng = best.StartLatLng![1];
                var distanceKm = best.DistanceMeters / 1000.0;
                var reason = $"type={best.Type}, distance={distanceKm:F2}km, radius<={clubRun.RadiusKm:F1}km";
                var foundItem = new AttendanceUserResult(user, true, best.Id, reason, false, null);
                found.Add(foundItem);

                await repository.UpsertClubRunAttendanceAsync(
                    new ClubRunAttendanceUpsert(
                        clubRun.Id,
                        runDate,
                        user.Id,
                        best.Id,
                        new DateTimeOffset(startLocal, TbilisiTimeZone.GetUtcOffset(startLocal)),
                        lat,
                        lng,
                        distanceKm,
                        foundItem.Reason),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing club run {ClubRunId} user {TelegramUserId}", clubRun.Id, user.TelegramUserId);
                var item = new AttendanceUserResult(user, false, null, "User processing error", true, ex.Message);
                errors.Add(item);
                await repository.UpsertClubRunAttendanceAsync(
                    new ClubRunAttendanceUpsert(clubRun.Id, runDate, user.Id, null, null, null, null, null, $"error: {ex.Message}"),
                    ct);
            }
        }

        await repository.SaveClubRunReportAsync(clubRun.Id, runDate, users.Count, found.Count, ct);
        var result = new ClubRunAttendanceResult(clubRun, runDate, found, notFound, errors);

        var destinationChatId = publishChatId ?? clubRun.ReportChatId;
        if (destinationChatId.HasValue)
        {
            await PublishReportToChatAsync(result, destinationChatId.Value, ct);
        }

        return result;
    }

    private static ActivityResponse? FindBestMatch(
        ClubRunRecord clubRun,
        List<ActivityResponse> activities,
        TimeSpan windowStart,
        TimeSpan windowEnd,
        TimeSpan targetStart)
    {
        var allowedTypes = ParseAllowedTypes(clubRun.AllowedActivityTypes);
        var candidates = activities
            .Where(a => allowedTypes.Contains(a.Type))
            .Where(a => a.StartLatLng is { Count: >= 2 })
            .Select(a => new { Activity = a, LocalStart = ParseStravaLocal(a.StartDateLocal) })
            .Where(x => x.LocalStart.TimeOfDay >= windowStart && x.LocalStart.TimeOfDay <= windowEnd)
            .Where(x => HaversineKm(x.Activity.StartLatLng![0], x.Activity.StartLatLng[1], clubRun.StartLat, clubRun.StartLng) <= clubRun.RadiusKm)
            .Where(x => !clubRun.MinDistanceKm.HasValue || x.Activity.DistanceMeters / 1000.0 >= clubRun.MinDistanceKm.Value)
            .Where(x => !clubRun.MaxDistanceKm.HasValue || x.Activity.DistanceMeters / 1000.0 <= clubRun.MaxDistanceKm.Value)
            .OrderBy(x => Math.Abs((x.LocalStart.TimeOfDay - targetStart).TotalMinutes))
            .ThenBy(x => x.LocalStart)
            .Select(x => x.Activity)
            .ToList();

        return candidates.FirstOrDefault();
    }

    public async Task PublishReportToChatAsync(ClubRunAttendanceResult result, long chatId, CancellationToken ct)
    {
        await telegramBot.SendMessage(chatId, BuildReportText(result), cancellationToken: ct);
    }

    public string BuildReportText(ClubRunAttendanceResult result)
    {
        var lines = new List<string>
        {
            $"{result.ClubRun.Name} attendance - {result.RunDate}",
            $"Found: {result.Found.Count} / {result.Found.Count + result.NotFound.Count + result.Errors.Count}",
            string.Empty,
            "Found:"
        };

        lines.AddRange(result.Found.Count == 0
            ? ["- none"]
            : result.Found.Select(x => $"- {DisplayName(x.User)}: https://www.strava.com/activities/{x.ActivityId}"));

        lines.Add(string.Empty);
        lines.Add("Not found:");
        lines.AddRange(result.NotFound.Count == 0
            ? ["- none"]
            : result.NotFound.Select(x => $"- {DisplayName(x.User)}"));

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Errors:");
            lines.AddRange(result.Errors.Select(x => $"- {DisplayName(x.User)} ({x.ErrorMessage})"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static HashSet<string> ParseAllowedTypes(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static TimeSpan ParseLocalTime(string value) => TimeSpan.ParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture);

    private static long ToUnixInTbilisi(DateTime localTime)
    {
        var offset = new DateTimeOffset(localTime, TbilisiTimeZone.GetUtcOffset(localTime));
        return offset.ToUnixTimeSeconds();
    }

    private static DateTime ParseStravaLocal(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt;
        }

        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
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
