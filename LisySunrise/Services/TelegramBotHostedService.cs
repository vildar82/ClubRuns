using System.Security.Cryptography;
using System.Text;
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
    IOptions<AppOptions> options,
    ILogger<TelegramBotHostedService> logger) : BackgroundService
{
    private readonly AppOptions _options = options.Value;

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

        if (!text.StartsWith('/'))
        {
            return;
        }

        var command = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

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
}
