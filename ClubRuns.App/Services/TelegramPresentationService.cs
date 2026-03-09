using System.Globalization;
using ClubRuns.App.Data;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClubRuns.App.Services;

public sealed class TelegramPresentationService(SqliteRepository repository)
{
    public async Task<string> BuildHelpTextAsync(long telegramUserId, CancellationToken ct)
    {
        var isConnectedToStrava = await repository.IsStravaConnectedByTelegramUserIdAsync(telegramUserId, ct);
        var lines = new List<string>
        {
            "ClubRuns commands:",
            "/start - register and connect Strava",
            $"Strava status: {(isConnectedToStrava ? "connected" : "not connected")}",
            "/runs - browse active runs and register with buttons",
            "/myregistrations - list your upcoming registrations",
            "/leaderboard - show run statistics by event results",
            "/myid - show your Telegram user id",
            "/help - show this help"
        };

        if (await repository.IsAdminAsync(telegramUserId, ct))
        {
            lines.Add(string.Empty);
            lines.Add("Admin commands:");
            lines.Add("/manage - create and edit run templates");
            lines.Add("/users - list users and Strava status");
            lines.Add("/clubs - list clubs");
            lines.Add("/run - choose club and run, then start attendance check");
            lines.Add("/admins - list current admins");
            lines.Add("/addadmin <id|@username> - add admin");
            lines.Add("/setstrava <client_id> <client_secret> - set Strava credentials");
            lines.Add("/stravastatus - show Strava credentials status");
            lines.Add("/importlegacy - start legacy import session");
            lines.Add("/importlegacydone - finish and import");
            lines.Add("/importlegacycancel - cancel import");
            lines.Add("/importlegacyexample - show import format example");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add($"How to become admin: ask an existing admin to run /addadmin {telegramUserId}");
        }

        var adminIds = await repository.GetAdminTelegramUserIdsAsync(ct);
        if (adminIds.Count > 0)
        {
            var adminLabels = new List<string>();
            foreach (var adminId in adminIds)
            {
                var adminUser = await repository.GetUserByTelegramIdAsync(adminId, ct);
                adminLabels.Add(adminUser is not null && !string.IsNullOrWhiteSpace(adminUser.TelegramUsername)
                    ? $"@{adminUser.TelegramUsername}"
                    : adminId.ToString(CultureInfo.InvariantCulture));
            }

            lines.Add(string.Empty);
            lines.Add("Current admins: " + string.Join(", ", adminLabels));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildNotAdminText(long telegramUserId)
    {
        return $"You are not an admin. Ask an existing admin to run /addadmin {telegramUserId}. Your Telegram user id: {telegramUserId}";
    }

    public string FormatUser(UserRecord user)
    {
        if (!string.IsNullOrWhiteSpace(user.TelegramUsername))
        {
            return $"@{user.TelegramUsername}";
        }

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? user.TelegramUserId.ToString(CultureInfo.InvariantCulture) : fullName;
    }

    public string BuildEditMenuText(ClubRunRecord clubRun)
    {
        return $"Editing club run:\n{BuildClubRunSummary(clubRun)}";
    }

    public string BuildClubRunSummary(ClubRunRecord clubRun)
    {
        return string.Join(
            Environment.NewLine,
            [
                $"#{clubRun.Id} {clubRun.Name}",
                $"Status: {(clubRun.IsActive ? "active" : "inactive")}",
                $"Schedule: {(DayOfWeek)clubRun.DayOfWeek} {clubRun.Hour:D2}:{clubRun.MinuteFrom:D2}-{clubRun.Hour:D2}:{clubRun.MinuteTo:D2}",
                $"Place: {clubRun.StartLat.ToString("0.000000", CultureInfo.InvariantCulture)}, {clubRun.StartLng.ToString("0.000000", CultureInfo.InvariantCulture)} | radius {clubRun.RadiusKm.ToString("0.##", CultureInfo.InvariantCulture)} km",
                $"Window: {clubRun.WindowStartLocal}-{clubRun.WindowEndLocal} | target {clubRun.TargetStartLocal}",
                $"Attendance check: {clubRun.CheckAtLocal}",
                $"Types: {clubRun.AllowedActivityTypes}",
                $"Distance: {FormatDistanceRange(clubRun.MinDistanceKm, clubRun.MaxDistanceKm)}",
                $"Report chat: {FormatReportChatId(clubRun.ReportChatId)}"
            ]);
    }

    public string FormatDistanceRange(double? minDistanceKm, double? maxDistanceKm)
    {
        var minText = minDistanceKm.HasValue ? $"{minDistanceKm.Value.ToString("0.##", CultureInfo.InvariantCulture)} km" : "any";
        var maxText = maxDistanceKm.HasValue ? $"{maxDistanceKm.Value.ToString("0.##", CultureInfo.InvariantCulture)} km" : "any";
        return $"{minText} - {maxText}";
    }

    public string FormatReportChatId(long? reportChatId)
    {
        return reportChatId?.ToString(CultureInfo.InvariantCulture) ?? "not set";
    }

    public InlineKeyboardMarkup BuildRunsListMarkup(IReadOnlyList<ClubRunRecord> runs)
    {
        var rows = runs
            .Select(run => new[] { InlineKeyboardButton.WithCallbackData($"{run.Name} | {(DayOfWeek)run.DayOfWeek} {run.Hour:D2}:{run.MinuteFrom:D2}", $"runs:view:{run.Id}") })
            .ToList();
        rows.Add([InlineKeyboardButton.WithCallbackData("My registrations", "runs:mine")]);
        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildRunsFooterMarkup()
    {
        return new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("Browse runs", "runs:list")]
        ]);
    }

    public InlineKeyboardMarkup BuildRunDetailsMarkup(long clubRunId, bool isRegistered)
    {
        return new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData(isRegistered ? "Leave" : "Join", $"runs:{(isRegistered ? "leave" : "join")}:{clubRunId}")],
            [InlineKeyboardButton.WithCallbackData("Back to runs", "runs:list")],
            [InlineKeyboardButton.WithCallbackData("My registrations", "runs:mine")]
        ]);
    }

    public InlineKeyboardMarkup BuildManageMainMenuMarkup()
    {
        return new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("Create club", "manage:clubs:create")],
            [InlineKeyboardButton.WithCallbackData("List clubs", "manage:clubs:list")],
            [InlineKeyboardButton.WithCallbackData("Delete club", "manage:clubs:delete")],
            [InlineKeyboardButton.WithCallbackData("Create run", "manage:runs:create")],
            [InlineKeyboardButton.WithCallbackData("Edit run", "manage:runs:edit")],
            [InlineKeyboardButton.WithCallbackData("List runs", "manage:runs:list")],
            [InlineKeyboardButton.WithCallbackData("Cancel", "manage:cancel")]
        ]);
    }

    public InlineKeyboardMarkup BuildClubSelectionMarkup(IReadOnlyList<ClubRecord> clubs, string mode)
    {
        var rows = clubs.Select(club => new[]
        {
            InlineKeyboardButton.WithCallbackData($"#{club.Id} {club.Name}", $"manage:clubsel:{mode}:{club.Id}")
        }).ToList();
        rows.Add([InlineKeyboardButton.WithCallbackData("Back", "manage:main")]);
        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildClubRunsInlineMarkup(IReadOnlyList<ClubRunRecord> runs)
    {
        var rows = runs
            .Select(run => new[]
            {
                InlineKeyboardButton.WithCallbackData($"#{run.Id} {run.Name}", $"manage:editrun:{run.Id}")
            })
            .ToList();

        rows.Add([InlineKeyboardButton.WithCallbackData("Back", "manage:main")]);
        rows.Add([InlineKeyboardButton.WithCallbackData("Cancel", "manage:cancel")]);
        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildAdminRunClubSelectionMarkup(IReadOnlyList<ClubRecord> clubs)
    {
        var rows = clubs.Select(club => new[]
        {
            InlineKeyboardButton.WithCallbackData($"#{club.Id} {club.Name}", $"adminrun:club:{club.Id}")
        }).ToList();
        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildAdminRunRunsMarkup(IReadOnlyList<ClubRunRecord> runs)
    {
        var rows = runs.Select(run => new[]
        {
            InlineKeyboardButton.WithCallbackData($"#{run.Id} {run.Name}", $"adminrun:run:{run.Id}")
        }).ToList();
        rows.Add([InlineKeyboardButton.WithCallbackData("Back to clubs", "adminrun:back")]);
        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildEditMenuMarkup(long clubRunId)
    {
        return new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("Name", $"manage:edit:name:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Day", $"manage:edit:day:{clubRunId}")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Hour", $"manage:edit:hour:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Minutes", $"manage:edit:minutes:{clubRunId}")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Place", $"manage:edit:place:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Window", $"manage:edit:window:{clubRunId}")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Check time", $"manage:edit:check:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Report chat", $"manage:edit:report:{clubRunId}")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Toggle active", $"manage:edit:toggle:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Run now", $"manage:edit:run:{clubRunId}")
            ],
            [
                InlineKeyboardButton.WithCallbackData("Back", $"manage:edit:back:{clubRunId}"),
                InlineKeyboardButton.WithCallbackData("Cancel", "manage:cancel")
            ]
        ]);
    }
}

