using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using LisySunrise.Config;
using LisySunrise.Data;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LisySunrise.Services;

public sealed class TelegramBotHostedService(
    ITelegramBotClient botClient,
    SqliteRepository repository,
    StravaApiClient stravaApi,
    AttendanceJobService attendanceJob,
    LegacyStatsImporterService legacyImporter,
    LeaderboardService leaderboardService,
    IOptions<AppOptions> options,
    ILogger<TelegramBotHostedService> logger) : BackgroundService
{
    private readonly AppOptions _options = options.Value;
    private readonly ConcurrentDictionary<long, LegacyImportSession> _importSessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Validate bot connectivity at startup and print account identity in logs.
        var me = await botClient.GetMe(stoppingToken);
        logger.LogInformation("Telegram bot started as @{Username}", me.Username);

        // Polling mode: Telegram pushes updates through this callback loop.
        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message] },
            cancellationToken: stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken ct)
    {
        // Polling should keep running even if one update handling cycle fails.
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

        if (!text.StartsWith('/'))
        {
            await HandleLegacyImportChunkAsync(chatId, telegramUserId, text, ct);
            return;
        }

        var rawCommand = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var command = rawCommand.Split('@')[0];

        // Commands are intentionally small for MVP; each one maps to a dedicated handler.
        switch (command)
        {
            case "/start":
                await HandleStartAsync(chatId, from, ct);
                break;
            case "/connect":
                await HandleConnectAsync(chatId, from.Id, ct);
                break;
            case "/status":
                await HandleStatusAsync(chatId, from.Id, ct);
                break;
            case "/users":
                await HandleUsersAsync(chatId, from.Id, ct);
                break;
            case "/run":
                await HandleRunAsync(chatId, from.Id, ct);
                break;
            case "/job":
                await HandleRunAsync(chatId, from.Id, ct);
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
        }
    }

    private async Task HandleStartAsync(long chatId, User from, CancellationToken ct)
    {
        // /start is idempotent: user row is inserted or refreshed with latest Telegram profile data.
        await repository.UpsertUserAsync(from.Id, from.Username, from.FirstName, from.LastName, ct);
        var url = await BuildConnectUrlAsync(from.Id, ct);
        var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Connect Strava", url));

        await botClient.SendMessage(
            chatId,
            "Welcome! Press the button to connect your Strava account.",
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task HandleConnectAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        // Require /start first because it creates a local user record used by callback mapping.
        var user = await repository.GetUserByTelegramIdAsync(telegramUserId, ct);
        if (user is null)
        {
            await botClient.SendMessage(chatId, "Use /start first.", cancellationToken: ct);
            return;
        }

        var url = await BuildConnectUrlAsync(telegramUserId, ct);
        var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Connect Strava", url));
        await botClient.SendMessage(chatId, "Connect your Strava account:", replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task HandleStatusAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        // Admin-only informational command.
        if (!IsAdmin(telegramUserId))
        {
            return;
        }

        var (total, connected) = await repository.GetUserStatsAsync(ct);
        await botClient.SendMessage(chatId, $"Users: {total}\nConnected to Strava: {connected}", cancellationToken: ct);
    }

    private async Task HandleUsersAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        // Admin-only users snapshot for quick operational checks.
        if (!IsAdmin(telegramUserId))
        {
            return;
        }

        var users = await repository.GetAllUsersWithAuthAsync(ct);
        var lines = users.Select(x => $"- {FormatUser(x.User)}: {(x.Auth is null ? "not connected" : "connected")}");
        var text = "Users:\n" + string.Join('\n', lines);
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task HandleRunAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        // Admin-only manual trigger for testing or ad-hoc reruns.
        if (!IsAdmin(telegramUserId))
        {
            return;
        }

        await botClient.SendMessage(chatId, "Running attendance job...", cancellationToken: ct);
        // Manual admin trigger executes exactly the same attendance pipeline as scheduler/job mode.
        var result = await attendanceJob.RunAsync(null, ct);
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
        if (!IsAdmin(telegramUserId))
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
        if (!IsAdmin(telegramUserId))
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
        if (!IsAdmin(telegramUserId))
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

    private async Task HandleLegacyImportChunkAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!IsAdmin(telegramUserId))
        {
            return;
        }

        if (!_importSessions.TryGetValue(telegramUserId, out var session))
        {
            return;
        }

        if (session.ChatId != chatId)
        {
            return;
        }

        session.Buffer.AppendLine(text);
        session.ChunksCount++;
        if (session.ChunksCount % 3 == 0)
        {
            await botClient.SendMessage(chatId, $"Received {session.ChunksCount} message chunks so far.", cancellationToken: ct);
        }
    }

    private bool IsAdmin(long telegramUserId) => _options.Telegram.AdminTelegramUserIds.Contains(telegramUserId);

    private async Task<string> BuildConnectUrlAsync(long telegramUserId, CancellationToken ct)
    {
        // state is persisted and then consumed on callback to prevent OAuth code substitution.
        var state = CreateStateToken(telegramUserId);
        await repository.SaveOAuthStateAsync(state, telegramUserId, ct);
        return stravaApi.BuildAuthorizeUrl(state);
    }

    private static string CreateStateToken(long telegramUserId)
    {
        // Include user id + random nonce + timestamp, then URL-safe base64 encode.
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        var random = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
        var payload = $"{telegramUserId}:{random}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
    }

    private static string FormatUser(UserRecord user)
    {
        // Prefer @username in reports; fallback to display name or telegram numeric id.
        if (!string.IsNullOrWhiteSpace(user.TelegramUsername))
        {
            return $"@{user.TelegramUsername}";
        }

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? user.TelegramUserId.ToString() : fullName;
    }

    private static string BuildLegacyExampleText()
    {
        return """
               🏃Sunny Leaderboard 2025/2026 😎

               Kosta (http://t.me/konstantin_kochura) 15 🌞🌞🌞🌞🌞🌥🌞🌞🌥🌞🌞🌞🌞🌞🌞
               Vasiliy (http://t.me/vasiliyizrossii) 12 🌞🌞🌞🌞🌥🌞🌥🌞🌥🌞🌞🌞
               Levan (http://t.me/levan_jabua) 11 🌞🌥🌞🌥🌞🌥🌞🌞🌞🌞🌞
               """;
    }

    private sealed class LegacyImportSession(long chatId, StringBuilder buffer, int chunksCount)
    {
        public long ChatId { get; } = chatId;
        public StringBuilder Buffer { get; } = buffer;
        public int ChunksCount { get; set; } = chunksCount;
    }
}
