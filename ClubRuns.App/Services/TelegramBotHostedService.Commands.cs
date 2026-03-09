using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService
{
    private async Task HandleStartAsync(long chatId, User from, CancellationToken ct)
    {
        await repository.UpsertUserAsync(from.Id, from.Username, from.FirstName, from.LastName, ct);

        try
        {
            var url = await BuildConnectUrlAsync(from.Id, ct);
            var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Connect Strava", url));
            await botClient.SendMessage(
                chatId,
                "Welcome to ClubRuns. Press the button to connect your Strava account.",
                replyMarkup: keyboard,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build Strava connect URL on /start");
            await botClient.SendMessage(
                chatId,
                "Profile is saved, but Strava credentials are not configured yet. Ask admin to run /setstrava.",
                cancellationToken: ct);
        }
    }

    private Task HandleManageAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        return manageFlow.OpenAsync(chatId, telegramUserId, ct);
    }

    private async Task HandleUsersAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var users = await repository.GetAllUsersWithAuthAsync(ct);
        var connectedCount = users.Count(x => x.Auth is not null);
        var lines = users.Select(x => $"- {presentation.FormatUser(x.User)}: {(x.Auth is null ? "not connected" : "connected")}");
        var text = $"Users: {users.Count} (connected: {connectedCount})\n" + string.Join('\n', lines);
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task HandleClubsAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var clubs = await repository.GetClubsAsync(ct);
        var text = clubs.Count == 0
            ? "No clubs available yet."
            : "Clubs:\n" + string.Join('\n', clubs.Select(x => $"- {x.Name} ({x.TimeZoneId})"));
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task ShowRunsMenuAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        await EnsureTelegramUserExistsAsync(telegramUserId, ct);
        var runs = await repository.GetClubRunsAsync(activeOnly: true, ct: ct);
        if (runs.Count == 0)
        {
            await botClient.SendMessage(chatId, "No active runs available.", cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(chatId, "Active runs:", replyMarkup: presentation.BuildRunsListMarkup(runs), cancellationToken: ct);
    }

    private async Task ShowMyRegistrationsAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        await EnsureTelegramUserExistsAsync(telegramUserId, ct);
        var user = await repository.GetUserByTelegramIdAsync(telegramUserId, ct);
        if (user is null)
        {
            await botClient.SendMessage(chatId, "Run /start first.", cancellationToken: ct);
            return;
        }

        var registrations = await repository.GetUserUpcomingRegistrationsAsync(user.Id, ct);
        if (registrations.Count == 0)
        {
            await botClient.SendMessage(chatId, "You have no upcoming registrations.", replyMarkup: presentation.BuildRunsFooterMarkup(), cancellationToken: ct);
            return;
        }

        var lines = registrations.Select(x => $"- {x.Run.Name} on {x.Event.EventDateLocal}");
        await botClient.SendMessage(chatId, "My registrations:\n" + string.Join('\n', lines), replyMarkup: presentation.BuildRunsFooterMarkup(), cancellationToken: ct);
    }

    private async Task HandleLeaderboardAsync(long chatId, CancellationToken ct)
    {
        var summary = await leaderboardService.BuildSummaryAsync(ct);
        await botClient.SendMessage(chatId, leaderboardService.BuildText(summary), cancellationToken: ct);
    }

    private async Task HandleImportLegacyStartAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        _importSessions[telegramUserId] = new LegacyImportSession(chatId, new StringBuilder(), 0);
        await botClient.SendMessage(
            chatId,
            "Legacy import started. Send leaderboard text in one or more messages, then run /importlegacydone. Use /importlegacycancel to abort.",
            cancellationToken: ct);
    }

    private async Task HandleImportLegacyDoneAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        if (!_importSessions.TryRemove(telegramUserId, out var session))
        {
            await botClient.SendMessage(chatId, "No active import session. Start with /importlegacy.", cancellationToken: ct);
            return;
        }

        var (imported, skipped) = await legacyImporter.ImportAsync(session.Buffer.ToString(), ct);
        await botClient.SendMessage(chatId, $"Legacy stats imported. Imported: {imported}, skipped lines: {skipped}.", cancellationToken: ct);
    }

    private async Task HandleImportLegacyCancelAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        _importSessions.TryRemove(telegramUserId, out _);
        await botClient.SendMessage(chatId, "Legacy import cancelled.", cancellationToken: ct);
    }

    private async Task HandleImportLegacyExampleAsync(long chatId, CancellationToken ct)
    {
        await botClient.SendMessage(chatId, BuildLegacyExampleText(), cancellationToken: ct);
    }

    private async Task HandleAddAdminAsync(long chatId, long actorTelegramUserId, string fullText, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, actorTelegramUserId, ct))
        {
            return;
        }

        var parts = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await botClient.SendMessage(chatId, "Usage: /addadmin <telegram_user_id|@username>", cancellationToken: ct);
            return;
        }

        var targetTelegramUserId = await ResolveTelegramUserIdAsync(parts[1].Trim(), ct);
        if (!targetTelegramUserId.HasValue)
        {
            await botClient.SendMessage(chatId, "Cannot resolve target admin. Use numeric Telegram user id or @username for an existing bot user.", cancellationToken: ct);
            return;
        }

        await repository.AddAdminAsync(targetTelegramUserId.Value, actorTelegramUserId, ct);
        await botClient.SendMessage(chatId, $"Admin added: {targetTelegramUserId.Value}", cancellationToken: ct);
    }

    private async Task HandleAdminsAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var adminIds = await repository.GetAdminTelegramUserIdsAsync(ct);
        var lines = new List<string> { "Admins:" };
        foreach (var id in adminIds)
        {
            var user = await repository.GetUserByTelegramIdAsync(id, ct);
            lines.Add(user is null ? $"- {id}" : $"- {id} ({presentation.FormatUser(user)})");
        }

        await botClient.SendMessage(chatId, string.Join('\n', lines), cancellationToken: ct);
    }

    private async Task HandleSetStravaAsync(long chatId, long telegramUserId, string fullText, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var parts = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await botClient.SendMessage(chatId, "Usage: /setstrava <client_id> <client_secret>", cancellationToken: ct);
            return;
        }

        await runtimeSettings.SetStravaCredentialsAsync(parts[1], parts[2], ct);
        await botClient.SendMessage(chatId, "Strava credentials updated in database settings.", cancellationToken: ct);
    }

    private async Task HandleStravaStatusAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var clientId = await runtimeSettings.GetStravaClientIdAsync(ct);
        var clientSecret = await runtimeSettings.GetStravaClientSecretAsync(ct);
        await botClient.SendMessage(
            chatId,
            $"Strava settings: ClientId={(string.IsNullOrWhiteSpace(clientId) ? "missing" : "set")}, ClientSecret={(string.IsNullOrWhiteSpace(clientSecret) ? "missing" : "set")}",
            cancellationToken: ct);
    }
}
