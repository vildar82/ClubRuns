using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TRC_Bot;

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
    private readonly ConcurrentDictionary<long, ManageSession> _manageSessions = new();

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
                if (await HandleManageMessageAsync(chatId, telegramUserId, text, ct))
                {
                    return;
                }

                if (await HandleLegacyImportChunkAsync(chatId, telegramUserId, text, ct))
                {
                    return;
                }

                await botClient.SendMessage(chatId, await BuildHelpTextAsync(telegramUserId, ct), cancellationToken: ct);
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
                "Welcome to TRC Bot. Press the button to connect your Strava account.",
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

    private async Task HandleManageAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var session = _manageSessions.GetOrAdd(telegramUserId, _ => new ManageSession());
        session.State = ManageState.MainMenu;
        session.SelectedClubRunId = null;
        await botClient.SendMessage(
            chatId,
            "Manage club runs:",
            replyMarkup: BuildKeyboard(["Create run", "Edit run", "List runs", "Cancel"]),
            cancellationToken: ct);
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

        var (targetDate, clubRunId, hasInvalidArg) = ParseRunArguments(fullText);
        if (hasInvalidArg)
        {
            await botClient.SendMessage(chatId, "Usage: /run [YYYY-MM-DD] [club_run_id]", cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(chatId, "Running attendance job...", cancellationToken: ct);
        var results = await attendanceJob.RunAsync(targetDate, clubRunId, chatId, skipExistingReports: false, ct);
        if (results.Count == 0)
        {
            await botClient.SendMessage(chatId, "No matching club runs found for the selected date.", cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(chatId, $"Done. Reports generated: {results.Count}", cancellationToken: ct);
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
            lines.Add(user is null ? $"- {id}" : $"- {id} ({FormatUser(user)})");
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

    private async Task<bool> HandleManageMessageAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!_manageSessions.TryGetValue(telegramUserId, out var session))
        {
            return false;
        }

        if (!await repository.IsAdminAsync(telegramUserId, ct))
        {
            _manageSessions.TryRemove(telegramUserId, out _);
            return false;
        }

        switch (session.State)
        {
            case ManageState.MainMenu:
                return await HandleManageMainMenuAsync(chatId, telegramUserId, text, session, ct);
            case ManageState.CreateName:
            case ManageState.CreateDayOfWeek:
            case ManageState.CreateHour:
            case ManageState.CreateMinuteFrom:
            case ManageState.CreateMinuteTo:
            case ManageState.CreateStartLat:
            case ManageState.CreateStartLng:
            case ManageState.CreateRadiusKm:
            case ManageState.CreateWindowStart:
            case ManageState.CreateWindowEnd:
            case ManageState.CreateTargetStart:
            case ManageState.CreateReportChat:
                return await HandleCreateFlowAsync(chatId, telegramUserId, text, session, ct);
            case ManageState.EditSelectRun:
            case ManageState.EditMenu:
            case ManageState.EditName:
            case ManageState.EditDayOfWeek:
            case ManageState.EditHour:
            case ManageState.EditMinuteFrom:
            case ManageState.EditMinuteTo:
            case ManageState.EditStartLat:
            case ManageState.EditStartLng:
            case ManageState.EditRadiusKm:
            case ManageState.EditWindowStart:
            case ManageState.EditWindowEnd:
            case ManageState.EditTargetStart:
            case ManageState.EditReportChat:
            case ManageState.MemberMenu:
            case ManageState.AddMember:
            case ManageState.RemoveMember:
                return await HandleEditFlowAsync(chatId, telegramUserId, text, session, ct);
            default:
                return false;
        }
    }

    private async Task<bool> HandleManageMainMenuAsync(long chatId, long telegramUserId, string text, ManageSession session, CancellationToken ct)
    {
        switch (text.Trim())
        {
            case "Create run":
                session.State = ManageState.CreateName;
                session.SelectedClubRunId = null;
                await botClient.SendMessage(chatId, "Run name:", replyMarkup: BuildKeyboard(["Cancel"]), cancellationToken: ct);
                return true;
            case "Edit run":
                session.State = ManageState.EditSelectRun;
                await SendClubRunsListAsync(chatId, ct);
                await botClient.SendMessage(chatId, "Send the club run id to edit.", replyMarkup: BuildKeyboard(["Back", "Cancel"]), cancellationToken: ct);
                return true;
            case "List runs":
                await SendClubRunsListAsync(chatId, ct);
                return true;
            case "Cancel":
                _manageSessions.TryRemove(telegramUserId, out _);
                await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                return true;
            default:
                await botClient.SendMessage(chatId, "Choose one of the menu options.", cancellationToken: ct);
                return true;
        }
    }

    private async Task<bool> HandleCreateFlowAsync(long chatId, long telegramUserId, string text, ManageSession session, CancellationToken ct)
    {
        if (text.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            _manageSessions.TryRemove(telegramUserId, out _);
            await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return true;
        }

        switch (session.State)
        {
            case ManageState.CreateName:
                session.Draft.Name = text.Trim();
                session.State = ManageState.CreateDayOfWeek;
                await botClient.SendMessage(chatId, "Day of week (0=Sunday ... 6=Saturday):", cancellationToken: ct);
                return true;
            case ManageState.CreateDayOfWeek:
                if (!int.TryParse(text, out var dayOfWeek) || dayOfWeek is < 0 or > 6)
                {
                    await botClient.SendMessage(chatId, "Enter a number between 0 and 6.", cancellationToken: ct);
                    return true;
                }
                session.Draft.DayOfWeek = dayOfWeek;
                session.State = ManageState.CreateHour;
                await botClient.SendMessage(chatId, "Hour (0-23):", cancellationToken: ct);
                return true;
            case ManageState.CreateHour:
                if (!int.TryParse(text, out var hour) || hour is < 0 or > 23)
                {
                    await botClient.SendMessage(chatId, "Enter an hour between 0 and 23.", cancellationToken: ct);
                    return true;
                }
                session.Draft.Hour = hour;
                session.State = ManageState.CreateMinuteFrom;
                await botClient.SendMessage(chatId, "Minute from (0-59):", cancellationToken: ct);
                return true;
            case ManageState.CreateMinuteFrom:
                if (!int.TryParse(text, out var minuteFrom) || minuteFrom is < 0 or > 59)
                {
                    await botClient.SendMessage(chatId, "Enter a minute between 0 and 59.", cancellationToken: ct);
                    return true;
                }
                session.Draft.MinuteFrom = minuteFrom;
                session.State = ManageState.CreateMinuteTo;
                await botClient.SendMessage(chatId, "Minute to (0-59, >= minute from):", cancellationToken: ct);
                return true;
            case ManageState.CreateMinuteTo:
                if (!int.TryParse(text, out var minuteTo) || minuteTo < session.Draft.MinuteFrom || minuteTo > 59)
                {
                    await botClient.SendMessage(chatId, "Enter a minute between current minute from and 59.", cancellationToken: ct);
                    return true;
                }
                session.Draft.MinuteTo = minuteTo;
                session.State = ManageState.CreateStartLat;
                await botClient.SendMessage(chatId, "Start latitude:", cancellationToken: ct);
                return true;
            case ManageState.CreateStartLat:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var startLat))
                {
                    await botClient.SendMessage(chatId, "Enter latitude as a number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.StartLat = startLat;
                session.State = ManageState.CreateStartLng;
                await botClient.SendMessage(chatId, "Start longitude:", cancellationToken: ct);
                return true;
            case ManageState.CreateStartLng:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var startLng))
                {
                    await botClient.SendMessage(chatId, "Enter longitude as a number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.StartLng = startLng;
                session.State = ManageState.CreateRadiusKm;
                await botClient.SendMessage(chatId, "Radius km:", cancellationToken: ct);
                return true;
            case ManageState.CreateRadiusKm:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var radiusKm) || radiusKm <= 0)
                {
                    await botClient.SendMessage(chatId, "Enter radius km as a positive number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.RadiusKm = radiusKm;
                session.State = ManageState.CreateWindowStart;
                await botClient.SendMessage(chatId, "Window start local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.CreateWindowStart:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.WindowStartLocal = text.Trim();
                session.State = ManageState.CreateWindowEnd;
                await botClient.SendMessage(chatId, "Window end local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.CreateWindowEnd:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.WindowEndLocal = text.Trim();
                session.State = ManageState.CreateTargetStart;
                await botClient.SendMessage(chatId, "Target start local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.CreateTargetStart:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.TargetStartLocal = text.Trim();
                session.State = ManageState.CreateReportChat;
                await botClient.SendMessage(chatId, "Report chat id or 'skip':", cancellationToken: ct);
                return true;
            case ManageState.CreateReportChat:
                if (!text.Equals("skip", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(text, out var reportChatId))
                    {
                        await botClient.SendMessage(chatId, "Enter numeric chat id or 'skip'.", cancellationToken: ct);
                        return true;
                    }
                    session.Draft.ReportChatId = reportChatId;
                }
                else
                {
                    session.Draft.ReportChatId = null;
                }

                var created = await repository.UpsertClubRunAsync(BuildClubRunUpsert(session.Draft, null, isActive: true), ct);
                _manageSessions[telegramUserId] = new ManageSession { State = ManageState.MainMenu };
                await botClient.SendMessage(
                    chatId,
                    $"Club run created: #{created.Id} {created.Name}",
                    replyMarkup: BuildKeyboard(["Create run", "Edit run", "List runs", "Cancel"]),
                    cancellationToken: ct);
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> HandleEditFlowAsync(long chatId, long telegramUserId, string text, ManageSession session, CancellationToken ct)
    {
        if (text.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            _manageSessions.TryRemove(telegramUserId, out _);
            await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return true;
        }

        if (text.Equals("Back", StringComparison.OrdinalIgnoreCase))
        {
            session.State = ManageState.MainMenu;
            session.SelectedClubRunId = null;
            await botClient.SendMessage(chatId, "Manage club runs:", replyMarkup: BuildKeyboard(["Create run", "Edit run", "List runs", "Cancel"]), cancellationToken: ct);
            return true;
        }

        if (session.State == ManageState.EditSelectRun)
        {
            if (!long.TryParse(text, out var clubRunId))
            {
                await botClient.SendMessage(chatId, "Send a numeric club run id.", cancellationToken: ct);
                return true;
            }

            var clubRun = await repository.GetClubRunByIdAsync(clubRunId, ct);
            if (clubRun is null)
            {
                await botClient.SendMessage(chatId, "Club run not found.", cancellationToken: ct);
                return true;
            }

            session.SelectedClubRunId = clubRun.Id;
            session.Draft.Load(clubRun);
            session.State = ManageState.EditMenu;
            await botClient.SendMessage(chatId, $"Editing #{clubRun.Id} {clubRun.Name}", replyMarkup: BuildKeyboard(["Edit name", "Edit day", "Edit hour", "Edit minutes", "Edit place", "Edit window", "Edit report chat", "Manage members", "Toggle active", "Run this", "Back", "Cancel"]), cancellationToken: ct);
            return true;
        }

        var selectedRun = session.SelectedClubRunId.HasValue ? await repository.GetClubRunByIdAsync(session.SelectedClubRunId.Value, ct) : null;
        if (selectedRun is null)
        {
            session.State = ManageState.EditSelectRun;
            await botClient.SendMessage(chatId, "Selected club run is missing. Send a run id.", cancellationToken: ct);
            return true;
        }

        switch (session.State)
        {
            case ManageState.EditMenu:
                return await HandleEditMenuAsync(chatId, text, session, selectedRun, ct);
            case ManageState.EditName:
                session.Draft.Name = text.Trim();
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditDayOfWeek:
                if (!int.TryParse(text, out var dayOfWeek) || dayOfWeek is < 0 or > 6)
                {
                    await botClient.SendMessage(chatId, "Enter a number between 0 and 6.", cancellationToken: ct);
                    return true;
                }
                session.Draft.DayOfWeek = dayOfWeek;
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditHour:
                if (!int.TryParse(text, out var hour) || hour is < 0 or > 23)
                {
                    await botClient.SendMessage(chatId, "Enter an hour between 0 and 23.", cancellationToken: ct);
                    return true;
                }
                session.Draft.Hour = hour;
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditMinuteFrom:
                if (!int.TryParse(text, out var minuteFrom) || minuteFrom is < 0 or > 59)
                {
                    await botClient.SendMessage(chatId, "Enter a minute between 0 and 59.", cancellationToken: ct);
                    return true;
                }
                session.Draft.MinuteFrom = minuteFrom;
                session.State = ManageState.EditMinuteTo;
                await botClient.SendMessage(chatId, "Minute to:", cancellationToken: ct);
                return true;
            case ManageState.EditMinuteTo:
                if (!int.TryParse(text, out var minuteTo) || minuteTo < session.Draft.MinuteFrom || minuteTo > 59)
                {
                    await botClient.SendMessage(chatId, "Enter a minute between minute from and 59.", cancellationToken: ct);
                    return true;
                }
                session.Draft.MinuteTo = minuteTo;
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditStartLat:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var startLat))
                {
                    await botClient.SendMessage(chatId, "Enter latitude as a number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.StartLat = startLat;
                session.State = ManageState.EditStartLng;
                await botClient.SendMessage(chatId, "Start longitude:", cancellationToken: ct);
                return true;
            case ManageState.EditStartLng:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var startLng))
                {
                    await botClient.SendMessage(chatId, "Enter longitude as a number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.StartLng = startLng;
                session.State = ManageState.EditRadiusKm;
                await botClient.SendMessage(chatId, "Radius km:", cancellationToken: ct);
                return true;
            case ManageState.EditRadiusKm:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var radiusKm) || radiusKm <= 0)
                {
                    await botClient.SendMessage(chatId, "Enter radius km as a positive number.", cancellationToken: ct);
                    return true;
                }
                session.Draft.RadiusKm = radiusKm;
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditWindowStart:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.WindowStartLocal = text.Trim();
                session.State = ManageState.EditWindowEnd;
                await botClient.SendMessage(chatId, "Window end local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.EditWindowEnd:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.WindowEndLocal = text.Trim();
                session.State = ManageState.EditTargetStart;
                await botClient.SendMessage(chatId, "Target start local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.EditTargetStart:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.TargetStartLocal = text.Trim();
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditReportChat:
                if (!text.Equals("skip", StringComparison.OrdinalIgnoreCase) && !long.TryParse(text, out var reportChatId))
                {
                    await botClient.SendMessage(chatId, "Enter numeric chat id or 'skip'.", cancellationToken: ct);
                    return true;
                }
                session.Draft.ReportChatId = text.Equals("skip", StringComparison.OrdinalIgnoreCase) ? null : long.Parse(text, CultureInfo.InvariantCulture);
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.MemberMenu:
                return await HandleMemberMenuAsync(chatId, text, session, selectedRun, ct);
            case ManageState.AddMember:
                return await HandleAddMemberAsync(chatId, text, session, selectedRun, ct);
            case ManageState.RemoveMember:
                return await HandleRemoveMemberAsync(chatId, text, session, selectedRun, ct);
            default:
                return false;
        }
    }

    private async Task<bool> HandleEditMenuAsync(long chatId, string text, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        switch (text.Trim())
        {
            case "Edit name":
                session.State = ManageState.EditName;
                await botClient.SendMessage(chatId, "New name:", cancellationToken: ct);
                return true;
            case "Edit day":
                session.State = ManageState.EditDayOfWeek;
                await botClient.SendMessage(chatId, "Day of week (0=Sunday ... 6=Saturday):", cancellationToken: ct);
                return true;
            case "Edit hour":
                session.State = ManageState.EditHour;
                await botClient.SendMessage(chatId, "Hour (0-23):", cancellationToken: ct);
                return true;
            case "Edit minutes":
                session.State = ManageState.EditMinuteFrom;
                await botClient.SendMessage(chatId, "Minute from (0-59):", cancellationToken: ct);
                return true;
            case "Edit place":
                session.State = ManageState.EditStartLat;
                await botClient.SendMessage(chatId, "Start latitude:", cancellationToken: ct);
                return true;
            case "Edit window":
                session.State = ManageState.EditWindowStart;
                await botClient.SendMessage(chatId, "Window start local (HH:mm):", cancellationToken: ct);
                return true;
            case "Edit report chat":
                session.State = ManageState.EditReportChat;
                await botClient.SendMessage(chatId, "Report chat id or 'skip':", cancellationToken: ct);
                return true;
            case "Manage members":
                session.State = ManageState.MemberMenu;
                await botClient.SendMessage(chatId, "Members:", replyMarkup: BuildKeyboard(["List members", "Add member", "Remove member", "Back", "Cancel"]), cancellationToken: ct);
                return true;
            case "Toggle active":
                var updatedRun = await repository.UpsertClubRunAsync(BuildClubRunUpsert(session.Draft, selectedRun.Id, !selectedRun.IsActive), ct);
                session.Draft.Load(updatedRun);
                await botClient.SendMessage(chatId, $"Run is now {(updatedRun.IsActive ? "active" : "inactive")}.", cancellationToken: ct);
                return true;
            case "Run this":
                var results = await attendanceJob.RunAsync(null, selectedRun.Id, chatId, skipExistingReports: false, ct);
                await botClient.SendMessage(chatId, results.Count == 0 ? "Nothing to run." : "Run report posted to this chat.", cancellationToken: ct);
                return true;
            default:
                await botClient.SendMessage(chatId, "Choose one of the edit actions.", cancellationToken: ct);
                return true;
        }
    }

    private async Task<bool> HandleMemberMenuAsync(long chatId, string text, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        switch (text.Trim())
        {
            case "List members":
                var members = await repository.GetClubRunMembersWithAuthAsync(selectedRun.Id, ct);
                var memberText = members.Count == 0
                    ? "No members."
                    : string.Join('\n', members.Select(x => $"- {FormatUser(x.User)}"));
                await botClient.SendMessage(chatId, memberText, cancellationToken: ct);
                return true;
            case "Add member":
                session.State = ManageState.AddMember;
                await botClient.SendMessage(chatId, "Send @username or Telegram user id.", cancellationToken: ct);
                return true;
            case "Remove member":
                session.State = ManageState.RemoveMember;
                await botClient.SendMessage(chatId, "Send @username or Telegram user id.", cancellationToken: ct);
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> HandleAddMemberAsync(long chatId, string text, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        var user = await ResolveUserAsync(text, ct);
        if (user is null)
        {
            await botClient.SendMessage(chatId, "User not found. Ask them to run /start first.", cancellationToken: ct);
            return true;
        }

        await repository.AddClubRunMemberAsync(selectedRun.Id, user.Id, ct);
        session.State = ManageState.MemberMenu;
        await botClient.SendMessage(chatId, $"Added {FormatUser(user)} to {selectedRun.Name}.", replyMarkup: BuildKeyboard(["List members", "Add member", "Remove member", "Back", "Cancel"]), cancellationToken: ct);
        return true;
    }

    private async Task<bool> HandleRemoveMemberAsync(long chatId, string text, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        var user = await ResolveUserAsync(text, ct);
        if (user is null)
        {
            await botClient.SendMessage(chatId, "User not found.", cancellationToken: ct);
            return true;
        }

        await repository.RemoveClubRunMemberAsync(selectedRun.Id, user.Id, ct);
        session.State = ManageState.MemberMenu;
        await botClient.SendMessage(chatId, $"Removed {FormatUser(user)} from {selectedRun.Name}.", replyMarkup: BuildKeyboard(["List members", "Add member", "Remove member", "Back", "Cancel"]), cancellationToken: ct);
        return true;
    }

    private async Task SaveEditedRunAsync(long chatId, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        await repository.UpsertClubRunAsync(BuildClubRunUpsert(session.Draft, selectedRun.Id, selectedRun.IsActive), ct);
        session.Draft.Load((await repository.GetClubRunByIdAsync(selectedRun.Id, ct))!);
        session.State = ManageState.EditMenu;
        await botClient.SendMessage(chatId, "Run updated.", replyMarkup: BuildKeyboard(["Edit name", "Edit day", "Edit hour", "Edit minutes", "Edit place", "Edit window", "Edit report chat", "Manage members", "Toggle active", "Run this", "Back", "Cancel"]), cancellationToken: ct);
    }

    private async Task SendClubRunsListAsync(long chatId, CancellationToken ct)
    {
        var runs = await repository.GetClubRunsAsync(activeOnly: false, ct);
        var text = runs.Count == 0
            ? "No club runs created yet."
            : string.Join('\n', runs.Select(x => $"#{x.Id} {x.Name} | day={(DayOfWeek)x.DayOfWeek} | time={x.Hour:D2}:{x.MinuteFrom:D2} | active={x.IsActive}"));
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task<bool> HandleLegacyImportChunkAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!await repository.IsAdminAsync(telegramUserId, ct))
        {
            return false;
        }

        if (!_importSessions.TryGetValue(telegramUserId, out var session) || session.ChatId != chatId)
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
        return $"You are not an admin. Ask an existing admin to run /addadmin {telegramUserId}. Your Telegram user id: {telegramUserId}";
    }

    private async Task<long?> ResolveTelegramUserIdAsync(string target, CancellationToken ct)
    {
        var user = await ResolveUserAsync(target, ct);
        return user?.TelegramUserId;
    }

    private async Task<UserRecord?> ResolveUserAsync(string target, CancellationToken ct)
    {
        if (long.TryParse(target, out var numericTelegramId))
        {
            return await repository.GetUserByTelegramIdAsync(numericTelegramId, ct);
        }

        var username = target.TrimStart('@');
        return string.IsNullOrWhiteSpace(username) ? null : await repository.GetUserByTelegramUsernameAsync(username, ct);
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
        return string.IsNullOrWhiteSpace(fullName) ? user.TelegramUserId.ToString(CultureInfo.InvariantCulture) : fullName;
    }

    private static (DateTime? targetDate, long? clubRunId, bool hasInvalidArg) ParseRunArguments(string fullText)
    {
        DateTime? targetDate = null;
        long? clubRunId = null;
        var parts = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        foreach (var part in parts)
        {
            if (DateTime.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                targetDate = parsedDate.Date;
                continue;
            }

            if (long.TryParse(part, out var parsedRunId))
            {
                clubRunId = parsedRunId;
                continue;
            }

            return (null, null, true);
        }

        return (targetDate, clubRunId, false);
    }

    private static bool IsValidTime(string value)
    {
        return TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _);
    }

    private static ClubRunUpsert BuildClubRunUpsert(ClubRunDraft draft, long? id, bool isActive)
    {
        return new ClubRunUpsert(
            id,
            draft.Name,
            Slugify(draft.Name),
            isActive,
            draft.DayOfWeek,
            draft.Hour,
            draft.MinuteFrom,
            draft.MinuteTo,
            draft.StartLat,
            draft.StartLng,
            draft.RadiusKm,
            draft.WindowStartLocal,
            draft.WindowEndLocal,
            draft.TargetStartLocal,
            "Run,TrailRun",
            null,
            null,
            draft.ReportChatId);
    }

    private static string Slugify(string value)
    {
        var cleaned = new string(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }
        return cleaned.Trim('-');
    }

    private static string BuildLegacyExampleText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "legacy_stats.example.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "Example file is missing: legacy_stats.example.txt";
    }

    private async Task<string> BuildHelpTextAsync(long telegramUserId, CancellationToken ct)
    {
        var isConnectedToStrava = await repository.IsStravaConnectedByTelegramUserIdAsync(telegramUserId, ct);
        var lines = new List<string>
        {
            "TRC Bot commands:",
            "/start - register and connect Strava",
            $"Strava status: {(isConnectedToStrava ? "connected" : "not connected")}",
            "/leaderboard - show combined legacy leaderboard",
            "/myid - show your Telegram user id",
            "/help - show this help"
        };

        if (await repository.IsAdminAsync(telegramUserId, ct))
        {
            lines.Add(string.Empty);
            lines.Add("Admin commands:");
            lines.Add("/manage - create and edit club runs with prompts");
            lines.Add("/users - list users and Strava status");
            lines.Add("/run [YYYY-MM-DD] [club_run_id] - run attendance check");
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

    private static ReplyKeyboardMarkup BuildKeyboard(string[] buttons)
    {
        return new ReplyKeyboardMarkup(buttons.Select(x => new KeyboardButton[] { x }))
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }

    private sealed class LegacyImportSession(long chatId, StringBuilder buffer, int chunksCount)
    {
        public long ChatId { get; } = chatId;
        public StringBuilder Buffer { get; } = buffer;
        public int ChunksCount { get; set; } = chunksCount;
    }
}


