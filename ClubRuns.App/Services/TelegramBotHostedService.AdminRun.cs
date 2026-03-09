using System.Globalization;
using ClubRuns.App.Data;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService
{
    private async Task HandleRunAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var clubs = await repository.GetClubsAsync(ct);
        if (clubs.Count == 0)
        {
            await botClient.SendMessage(chatId, "No clubs available yet. Create a club first with /manage.", cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(
            chatId,
            "Choose a club to run attendance check:",
            replyMarkup: presentation.BuildAdminRunClubSelectionMarkup(clubs),
            cancellationToken: ct);
    }

    private async Task HandleAdminRunCallbackAsync(long chatId, string data, CancellationToken ct)
    {
        if (data == "adminrun:back")
        {
            var clubs = await repository.GetClubsAsync(ct);
            await botClient.SendMessage(
                chatId,
                clubs.Count == 0 ? "No clubs available yet." : "Choose a club to run attendance check:",
                replyMarkup: clubs.Count == 0 ? null : presentation.BuildAdminRunClubSelectionMarkup(clubs),
                cancellationToken: ct);
            return;
        }

        if (data.StartsWith("adminrun:club:", StringComparison.Ordinal))
        {
            if (!long.TryParse(data["adminrun:club:".Length..], out var clubId))
            {
                return;
            }

            var club = await repository.GetClubByIdAsync(clubId, ct);
            if (club is null)
            {
                await botClient.SendMessage(chatId, "Club not found.", cancellationToken: ct);
                return;
            }

            var runs = await repository.GetClubRunsAsync(activeOnly: true, clubId, ct);
            if (runs.Count == 0)
            {
                await botClient.SendMessage(chatId, $"No active runs in {club.Name}.", cancellationToken: ct);
                return;
            }

            await botClient.SendMessage(
                chatId,
                $"Choose a run in {club.Name}:",
                replyMarkup: presentation.BuildAdminRunRunsMarkup(runs),
                cancellationToken: ct);
            return;
        }

        if (data.StartsWith("adminrun:run:", StringComparison.Ordinal))
        {
            if (!long.TryParse(data["adminrun:run:".Length..], out var clubRunId))
            {
                return;
            }

            var clubRun = await repository.GetClubRunByIdAsync(clubRunId, ct);
            if (clubRun is null)
            {
                await botClient.SendMessage(chatId, "Run not found.", cancellationToken: ct);
                return;
            }

            var club = await repository.GetClubByIdAsync(clubRun.ClubId, ct);
            if (club is null)
            {
                await botClient.SendMessage(chatId, "Club not found for selected run.", cancellationToken: ct);
                return;
            }

            var targetDate = GetManualRunDate(clubRun, club.TimeZoneId);
            var targetDateText = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await botClient.SendMessage(chatId, $"Running attendance for {club.Name} / {clubRun.Name} on {targetDateText}...", cancellationToken: ct);

            var results = await attendanceJob.RunAsync(targetDate.ToDateTime(TimeOnly.MinValue), clubRun.Id, chatId, skipExistingReports: false, ct);
            if (results.Count == 0)
            {
                await botClient.SendMessage(chatId, "No matching run was processed.", cancellationToken: ct);
                return;
            }

            await botClient.SendMessage(chatId, $"Done. Reports generated: {results.Count}", cancellationToken: ct);
        }
    }

    private static DateOnly GetManualRunDate(ClubRunRecord clubRun, string timeZoneId)
    {
        var timeZone = ResolveTimeZoneForManualRun(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var currentDate = DateOnly.FromDateTime(nowLocal.DateTime);
        var daysBack = ((int)currentDate.DayOfWeek - clubRun.DayOfWeek + 7) % 7;
        return currentDate.AddDays(-daysBack);
    }

    private static TimeZoneInfo ResolveTimeZoneForManualRun(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            foreach (var fallbackId in new[] { "Asia/Tbilisi", "Georgian Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(fallbackId);
                }
                catch
                {
                }
            }

            throw new InvalidOperationException($"Cannot resolve timezone '{timeZoneId}'.");
        }
    }
}
