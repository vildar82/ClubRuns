using System.Collections.Concurrent;
using ClubRuns.App.Config;
using ClubRuns.App.Data;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService(
    ITelegramBotClient botClient,
    SqliteRepository repository,
    StravaApiClient stravaApi,
    RuntimeSettingsService runtimeSettings,
    AttendanceJobService attendanceJob,
    IOptions<AppOptions> options,
    LegacyStatsImporterService legacyImporter,
    LeaderboardService leaderboardService,
    TelegramPresentationService presentation,
    TelegramManageFlowService manageFlow,
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
            receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
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
        if (update.CallbackQuery is { From: not null } callbackQuery)
        {
            await HandleCallbackQueryAsync(callbackQuery, ct);
            return;
        }

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
                if (await HandleManageMessageAsync(chatId, telegramUserId, text, ct))
                {
                    return;
                }

                if (await HandleLegacyImportChunkAsync(chatId, telegramUserId, text, ct))
                {
                    return;
                }

                await botClient.SendMessage(chatId, await presentation.BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
                return;
            }

            var rawCommand = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var command = rawCommand.Split('@')[0];

            switch (command)
            {
                case "/start":
                    await HandleStartAsync(chatId, from, ct);
                    break;
                case "/manage":
                    await HandleManageAsync(chatId, telegramUserId, ct);
                    break;
                case "/users":
                    await HandleUsersAsync(chatId, telegramUserId, ct);
                    break;
                case "/clubs":
                    await HandleClubsAsync(chatId, telegramUserId, ct);
                    break;
                case "/runs":
                    await ShowRunsMenuAsync(chatId, from.Id, ct);
                    break;
                case "/myregistrations":
                    await ShowMyRegistrationsAsync(chatId, from.Id, ct);
                    break;
                case "/join":
                case "/leave":
                    await botClient.SendMessage(chatId, "Use /runs and the inline buttons to join or leave runs.", cancellationToken: ct);
                    break;
                case "/run":
                case "/job":
                    await HandleRunAsync(chatId, telegramUserId, ct);
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
                    await botClient.SendMessage(chatId, await presentation.BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
                    break;
                default:
                    await botClient.SendMessage(chatId, await presentation.BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
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

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var telegramUserId = callbackQuery.From.Id;
        var chatId = callbackQuery.Message?.Chat.Id;
        if (chatId is null || string.IsNullOrWhiteSpace(callbackQuery.Data))
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        try
        {
            if (callbackQuery.Data.StartsWith("runs:", StringComparison.Ordinal))
            {
                await HandleRunsCallbackAsync(chatId.Value, telegramUserId, callbackQuery.Data, ct);
                await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
                return;
            }

            if (callbackQuery.Data.StartsWith("adminrun:", StringComparison.Ordinal))
            {
                if (!await repository.IsAdminAsync(telegramUserId, ct))
                {
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "Admins only", cancellationToken: ct);
                    await botClient.SendMessage(chatId.Value, presentation.BuildNotAdminText(telegramUserId), cancellationToken: ct);
                    return;
                }

                await HandleAdminRunCallbackAsync(chatId.Value, callbackQuery.Data, ct);
                await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
                return;
            }

            if (!await repository.IsAdminAsync(telegramUserId, ct))
            {
                await botClient.AnswerCallbackQuery(callbackQuery.Id, "Admins only", cancellationToken: ct);
                await botClient.SendMessage(chatId.Value, presentation.BuildNotAdminText(telegramUserId), cancellationToken: ct);
                return;
            }

            await HandleManageCallbackAsync(chatId.Value, telegramUserId, callbackQuery, ct);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process callback query for user {TelegramUserId}. Data: {Data}", telegramUserId, callbackQuery.Data);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Error", cancellationToken: ct);
            await botClient.SendMessage(chatId.Value, "Unexpected error while processing your action. Please try again.", cancellationToken: ct);
        }
    }
}

