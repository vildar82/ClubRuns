using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LisiSunrise;

public sealed class TelegramBotHostedService(
    ITelegramBotClient botClient,
    SqliteRepository repository,
    StravaApiClient stravaApi,
    RuntimeSettingsService runtimeSettings,
    AttendanceJobService attendanceJob,
    LegacyStatsImporterService legacyImporter,
    LeaderboardService leaderboardService,
    ILogger<TelegramBotHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, LegacyImportSession> _importSessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await botClient.GetMe(stoppingToken);
        logger.LogInformation("Telegram bot started as @{Username}", me.Username);

        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message] },
            cancellationToken: stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Telegram polling error");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        if (update.Message?.Text is not { } text || update.Message.From is null)
        {
            return;
        }

        var from = update.Message.From;
        var chatId = update.Message.Chat.Id;
        var telegramUserId = from.Id;

        try
        {
            if (!text.StartsWith('/'))
            {
                var consumedByImport = await HandleLegacyImportChunkAsync(chatId, telegramUserId, text, ct);
                if (!consumedByImport)
                {
                    await botClient.SendMessage(chatId, await BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
                }

                return;
            }

            var rawCommand = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var command = rawCommand.Split('@')[0];

            switch (command)
            {
                case "/start":
                    await HandleStartAsync(chatId, from, ct);
                    break;
                case "/connect":
                    await botClient.SendMessage(chatId, "Use /start to register and connect Strava.", cancellationToken: ct);
                    break;
                case "/users":
                    await HandleUsersAsync(chatId, telegramUserId, ct);
                    break;
                case "/run":
                case "/job":
                    await HandleRunAsync(chatId, telegramUserId, text, ct);
                    break;
                case "/leaderboard":
                    await HandleLeaderboardAsync(chatId, ct);
                    break;
                case "/importlegacy":
                    await HandleImportLegacyStartAsync(chatId, telegramUserId, ct);
                    break;
                case "/importlegacydone":
                    await HandleImportLegacyDoneAsync(chatId, telegramUserId, ct);
                    break;
                case "/importlegacycancel":
                    await HandleImportLegacyCancelAsync(chatId, telegramUserId, ct);
                    break;
                case "/importlegacyexample":
                    await HandleImportLegacyExampleAsync(chatId, ct);
                    break;
                case "/addadmin":
                    await HandleAddAdminAsync(chatId, telegramUserId, text, ct);
                    break;
                case "/admins":
                    await HandleAdminsAsync(chatId, telegramUserId, ct);
                    break;
                case "/setstrava":
                    await HandleSetStravaAsync(chatId, telegramUserId, text, ct);
                    break;
                case "/stravastatus":
                    await HandleStravaStatusAsync(chatId, telegramUserId, ct);
                    break;
                case "/myid":
                    await botClient.SendMessage(chatId, $"Your Telegram user id: {telegramUserId}", cancellationToken: ct);
                    break;
                case "/help":
                    await botClient.SendMessage(chatId, await BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
                    break;
                default:
                    await botClient.SendMessage(chatId, await BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Telegram update for user {TelegramUserId}. Text: {Text}", telegramUserId, text);

            var userMessage = ex is InvalidOperationException
                ? ex.Message
                : "Unexpected error while processing your request. Please try again.";

            try
            {
                await botClient.SendMessage(chatId, userMessage, cancellationToken: ct);
            }
            catch (Exception sendEx)
            {
                logger.LogWarning(sendEx, "Failed to send error message to chat {ChatId}", chatId);
            }
        }
    }

    private async Task HandleStartAsync(long chatId, User from, CancellationToken ct)
    {
        await repository.UpsertUserAsync(from.Id, from.Username, from.FirstName, from.LastName, ct);

        try
        {
            var url = await BuildConnectUrlAsync(from.Id, ct);
            var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Connect Strava", url));

            await botClient.SendMessage(
                chatId,
                "Welcome! Press the button to connect your Strava account.",
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


    private async Task HandleUsersAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var users = await repository.GetAllUsersWithAuthAsync(ct);
        var connectedCount = users.Count(x => x.Auth is not null);
        var lines = users.Select(x => $"- {FormatUser(x.User)}: {(x.Auth is null ? "not connected" : "connected")}");
        var text = $"Users: {users.Count} (connected: {connectedCount})\n" + string.Join('\n', lines);
        await botClient.SendMessage(chatId, text, cancellationToken: ct);

    }

    private async Task HandleRunAsync(long chatId, long telegramUserId, string fullText, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var targetDate = ParseRunDate(fullText);
        if (fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 && !targetDate.HasValue)
        {
            await botClient.SendMessage(chatId, "Invalid date format. Use: /run YYYY-MM-DD", cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(chatId, "Running attendance job...", cancellationToken: ct);
        var result = await attendanceJob.RunAsync(targetDate, ct);
        await botClient.SendMessage(
            chatId,
            $"Done. Found: {result.Found.Count}, Not found: {result.NotFound.Count}, Errors: {result.Errors.Count}",
            cancellationToken: ct);
    }
    private async Task HandleLeaderboardAsync(long chatId, CancellationToken ct)
    {
        var summary = await leaderboardService.BuildSummaryAsync(ct);
        var text = leaderboardService.BuildText(summary);
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
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
        await botClient.SendMessage(
            chatId,
            $"Legacy stats imported. Imported: {imported}, skipped lines: {skipped}.",
            cancellationToken: ct);
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

        var target = parts[1].Trim();
        var targetTelegramUserId = await ResolveTelegramUserIdAsync(target, ct);
        if (!targetTelegramUserId.HasValue)
        {
            await botClient.SendMessage(
                chatId,
                "Cannot resolve target admin. Use numeric Telegram user id, or @username for users who already ran /start.",
                cancellationToken: ct);
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
        if (adminIds.Count == 0)
        {
            await botClient.SendMessage(chatId, "No admins found.", cancellationToken: ct);
            return;
        }

        var lines = new List<string> { "Admins:" };
        foreach (var id in adminIds)
        {
            var user = await repository.GetUserByTelegramIdAsync(id, ct);
            if (user is not null)
            {
                lines.Add($"- {id} ({FormatUser(user)})");
            }
            else
            {
                lines.Add($"- {id}");
            }
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
        var idStatus = string.IsNullOrWhiteSpace(clientId) ? "missing" : "set";
        var secretStatus = string.IsNullOrWhiteSpace(clientSecret) ? "missing" : "set";
        await botClient.SendMessage(chatId, $"Strava settings: ClientId={idStatus}, ClientSecret={secretStatus}", cancellationToken: ct);
    }

    private async Task<bool> HandleLegacyImportChunkAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!await repository.IsAdminAsync(telegramUserId, ct))
        {
            return false;
        }

        if (!_importSessions.TryGetValue(telegramUserId, out var session))
        {
            return false;
        }

        if (session.ChatId != chatId)
        {
            return false;
        }

        session.Buffer.AppendLine(text);
        session.ChunksCount++;
        if (session.ChunksCount % 3 == 0)
        {
            await botClient.SendMessage(chatId, $"Received {session.ChunksCount} message chunks so far.", cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> EnsureAdminOrReplyAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (await repository.IsAdminAsync(telegramUserId, ct))
        {
            return true;
        }

        await botClient.SendMessage(chatId, BuildNotAdminText(telegramUserId), cancellationToken: ct);
        return false;
    }

    private static string BuildNotAdminText(long telegramUserId)
    {
        return $"""
                You are not an admin.

                How to become admin:
                1) Ask an existing admin to run: /addadmin {telegramUserId}
                2) If there is no admin yet, set your id in appsettings Telegram.AdminTelegramUserIds and restart the app.

                Your Telegram user id: {telegramUserId}
                """;
    }

    private async Task<long?> ResolveTelegramUserIdAsync(string target, CancellationToken ct)
    {
        if (long.TryParse(target, out var numericChatId))
        {
            return numericChatId;
        }

        var username = target.TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var user = await repository.GetUserByTelegramUsernameAsync(username, ct);
        return user?.TelegramUserId;
    }

    private async Task<string> BuildConnectUrlAsync(long telegramUserId, CancellationToken ct)
    {
        var state = CreateStateToken(telegramUserId);
        await repository.SaveOAuthStateAsync(state, telegramUserId, ct);
        return await stravaApi.BuildAuthorizeUrlAsync(state, ct);
    }

    private static string CreateStateToken(long telegramUserId)
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        var random = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
        var payload = $"{telegramUserId}:{random}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
    }

    private static string FormatUser(UserRecord user)
    {
        if (!string.IsNullOrWhiteSpace(user.TelegramUsername))
        {
            return $"@{user.TelegramUsername}";
        }

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? user.TelegramUserId.ToString() : fullName;
    }


    private static DateTime? ParseRunDate(string fullText)
    {
        var parts = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (DateTime.TryParseExact(parts[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed.Date;
        }

        return null;
    }

    private static string BuildLegacyExampleText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "legacy_stats.example.txt");
        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        return "Example file is missing: legacy_stats.example.txt";
    }

    private sealed class LegacyImportSession(long chatId, StringBuilder buffer, int chunksCount)
    {
        public long ChatId { get; } = chatId;
        public StringBuilder Buffer { get; } = buffer;
        public int ChunksCount { get; set; } = chunksCount;
    }

    private async Task<string> BuildHelpTextAsync(long telegramUserId, CancellationToken ct)
    {
        var isConnectedToStrava = await repository.IsStravaConnectedByTelegramUserIdAsync(telegramUserId, ct);
        var stravaStatus = isConnectedToStrava ? "connected" : "not connected";
        var lines = new List<string>
        {
            "LisiSunrise bot commands:",
            "/start - register or update your profile",
            $"Strava status: {stravaStatus}",
            "/leaderboard - show combined leaderboard",
            "/myid - show your Telegram user id",
            "/help - show this help"
        };

        var isAdmin = await repository.IsAdminAsync(telegramUserId, ct);
        if (isAdmin)
        {
            lines.Add(string.Empty);
            lines.Add("Admin commands:");
            lines.Add("/users - list users and Strava status");
            lines.Add("/run or /job [YYYY-MM-DD] - run attendance check (optional target date)");
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
            lines.Add("Admin commands are hidden.");
            lines.Add($"How to become admin: ask an existing admin to run /addadmin {telegramUserId}");
        }

        var adminIds = await repository.GetAdminTelegramUserIdsAsync(ct);
        if (adminIds.Count > 0)
        {
            var adminLabels = new List<string>();
            foreach (var adminId in adminIds)
            {
                var adminUser = await repository.GetUserByTelegramIdAsync(adminId, ct);
                if (adminUser is not null && !string.IsNullOrWhiteSpace(adminUser.TelegramUsername))
                {
                    adminLabels.Add($"@{adminUser.TelegramUsername}");
                }
                else
                {
                    adminLabels.Add(adminId.ToString());
                }
            }

            lines.Add(string.Empty);
            lines.Add("Current admins: " + string.Join(", ", adminLabels));
        }

        return string.Join(Environment.NewLine, lines);
    }
}







