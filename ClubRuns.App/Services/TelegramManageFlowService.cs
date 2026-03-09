using System.Collections.Concurrent;
using System.Globalization;
using ClubRuns.App.Config;
using ClubRuns.App.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClubRuns.App.Services;

public sealed class TelegramManageFlowService(
    ITelegramBotClient botClient,
    SqliteRepository repository,
    TelegramPresentationService presentation,
    IServiceProvider services)
{
    private readonly ConcurrentDictionary<long, ManageSession> _sessions = new();

    public async Task OpenAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (!await EnsureAdminOrReplyAsync(chatId, telegramUserId, ct))
        {
            return;
        }

        var session = _sessions.GetOrAdd(telegramUserId, _ => new ManageSession());
        session.State = ManageState.MainMenu;
        session.SelectedClubId = null;
        session.SelectedClubRunId = null;
        await botClient.SendMessage(chatId, "Manage clubs and runs:", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
    }
    public async Task<bool> HandleMessageAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(telegramUserId, out var session))
        {
            return false;
        }

        if (!await repository.IsAdminAsync(telegramUserId, ct))
        {
            _sessions.TryRemove(telegramUserId, out _);
            return false;
        }

        switch (session.State)
        {
            case ManageState.MainMenu:
                await botClient.SendMessage(chatId, "Use the inline buttons below.", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                return true;
            case ManageState.CreateClubName:
                session.Draft.Name = text.Trim();
                if (string.IsNullOrWhiteSpace(session.Draft.Name))
                {
                    await botClient.SendMessage(chatId, "Club name cannot be empty.", cancellationToken: ct);
                    return true;
                }

                session.State = ManageState.CreateClubTimeZone;
                await botClient.SendMessage(chatId, "Club time zone id. Example: Asia/Tbilisi. Send 'skip' to use the default.", cancellationToken: ct);
                return true;
            case ManageState.CreateClubTimeZone:
                var clubName = session.Draft.Name;
                var defaultTimeZoneId = services.GetRequiredService<IOptions<AppOptions>>().Value.Schedule.TimeZoneId;
                var timeZoneId = IsSkipText(text) ? defaultTimeZoneId : text.Trim();
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                }
                catch
                {
                    await botClient.SendMessage(chatId, $"Unknown time zone id. Example: Asia/Tbilisi. Send 'skip' to use {defaultTimeZoneId}.", cancellationToken: ct);
                    return true;
                }

                var createdClub = await repository.UpsertClubAsync(null, clubName, Slugify(clubName), timeZoneId, true, ct);
                session.State = ManageState.MainMenu;
                session.SelectedClubId = createdClub.Id;
                await botClient.SendMessage(chatId, $"Club created: #{createdClub.Id} {createdClub.Name} ({createdClub.TimeZoneId})", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                return true;
            case ManageState.CreateSelectClub:
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
            case ManageState.CreateCheckAt:
            case ManageState.CreateReportChat:
                return await HandleCreateFlowAsync(chatId, telegramUserId, text, session, ct);
            case ManageState.EditSelectClub:
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
            case ManageState.EditCheckAt:
            case ManageState.EditReportChat:
                return await HandleEditFlowAsync(chatId, telegramUserId, text, session, ct);
            default:
                return false;
        }
    }

    public async Task HandleCallbackAsync(long chatId, long telegramUserId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var data = callbackQuery.Data!;
        var session = _sessions.GetOrAdd(telegramUserId, _ => new ManageSession());

        if (data == "manage:main")
        {
            session.State = ManageState.MainMenu;
            session.SelectedClubId = null;
            session.SelectedClubRunId = null;
            await botClient.SendMessage(chatId, "Manage club runs:", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
            return;
        }

        if (data == "manage:cancel")
        {
            _sessions.TryRemove(telegramUserId, out _);
            await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return;
        }

        if (data == "manage:clubs:list")
        {
            await HandleClubsAdminListAsync(chatId, ct);
            await botClient.SendMessage(chatId, "Manage clubs and runs:", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
            return;
        }

        if (data == "manage:clubs:create")
        {
            session.State = ManageState.CreateClubName;
            session.SelectedClubId = null;
            session.SelectedClubRunId = null;
            await botClient.SendMessage(chatId, "Club name:", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return;
        }

        if (data == "manage:clubs:delete")
        {
            await SendClubSelectionAsync(chatId, "Choose a club to delete:", "delete", ct);
            session.State = ManageState.MainMenu;
            return;
        }

        if (data == "manage:runs:list")
        {
            await SendClubSelectionAsync(chatId, "Choose a club to list runs:", "list", ct);
            session.State = ManageState.MainMenu;
            return;
        }

        if (data == "manage:runs:create")
        {
            session.State = ManageState.CreateSelectClub;
            session.SelectedClubRunId = null;
            await SendClubSelectionAsync(chatId, "Choose a club for the new run:", "create", ct);
            return;
        }

        if (data == "manage:runs:edit")
        {
            session.State = ManageState.EditSelectClub;
            await SendClubSelectionAsync(chatId, "Choose a club to edit runs:", "edit", ct);
            return;
        }
        if (data.StartsWith("manage:clubsel:", StringComparison.Ordinal))
        {
            var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !long.TryParse(parts[3], out var clubId))
            {
                return;
            }

            var club = await repository.GetClubByIdAsync(clubId, ct);
            if (club is null)
            {
                await botClient.SendMessage(chatId, "Club not found.", cancellationToken: ct);
                return;
            }

            session.SelectedClubId = clubId;
            session.Draft.ClubId = clubId;

            switch (parts[2])
            {
                case "create":
                    session.State = ManageState.CreateName;
                    await botClient.SendMessage(chatId, $"Run name for {club.Name}:", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "edit":
                    session.State = ManageState.EditSelectRun;
                    var clubRuns = await repository.GetClubRunsAsync(activeOnly: false, clubId, ct);
                    await botClient.SendMessage(chatId, $"Choose a run to edit in {club.Name}:", replyMarkup: presentation.BuildClubRunsInlineMarkup(clubRuns), cancellationToken: ct);
                    return;
                case "list":
                    await SendClubRunsListAsync(chatId, clubId, ct);
                    await botClient.SendMessage(chatId, "Manage clubs and runs:", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                    return;
                case "delete":
                    if (await repository.ClubHasRunsAsync(clubId, ct))
                    {
                        await botClient.SendMessage(chatId, $"Cannot delete {club.Name}. Delete its runs first.", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                        return;
                    }

                    if (await repository.DeleteClubAsync(clubId, ct))
                    {
                        session.SelectedClubId = null;
                        await botClient.SendMessage(chatId, $"Club deleted: {club.Name}", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Club was not deleted.", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                    }
                    return;
            }
        }
        if (data.StartsWith("manage:editrun:", StringComparison.Ordinal))
        {
            if (!long.TryParse(data["manage:editrun:".Length..], out var clubRunId))
            {
                return;
            }

            var clubRun = await repository.GetClubRunByIdAsync(clubRunId, ct);
            if (clubRun is null)
            {
                await botClient.SendMessage(chatId, "Club run not found.", cancellationToken: ct);
                return;
            }

            session.SelectedClubRunId = clubRun.Id;
            session.Draft.Load(clubRun);
            session.State = ManageState.EditMenu;
            await botClient.SendMessage(chatId, presentation.BuildEditMenuText(clubRun), replyMarkup: presentation.BuildEditMenuMarkup(clubRun.Id), cancellationToken: ct);
            return;
        }

        if (data.StartsWith("manage:edit:", StringComparison.Ordinal))
        {
            var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !long.TryParse(parts[3], out var clubRunId))
            {
                return;
            }

            var clubRun = await repository.GetClubRunByIdAsync(clubRunId, ct);
            if (clubRun is null)
            {
                await botClient.SendMessage(chatId, "Club run not found.", cancellationToken: ct);
                return;
            }

            session.SelectedClubRunId = clubRun.Id;
            session.Draft.Load(clubRun);

            switch (parts[2])
            {
                case "name":
                    session.State = ManageState.EditName;
                    await botClient.SendMessage(chatId, $"New name (current: {clubRun.Name}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "day":
                    session.State = ManageState.EditDayOfWeek;
                    await botClient.SendMessage(chatId, $"Day of week (0=Sunday ... 6=Saturday, current: {clubRun.DayOfWeek}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "hour":
                    session.State = ManageState.EditHour;
                    await botClient.SendMessage(chatId, $"Hour (0-23, current: {clubRun.Hour}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "minutes":
                    session.State = ManageState.EditMinuteFrom;
                    await botClient.SendMessage(chatId, $"Minute from (0-59, current: {clubRun.MinuteFrom}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "place":
                    session.State = ManageState.EditStartLat;
                    await botClient.SendMessage(chatId, $"Start latitude (current: {clubRun.StartLat.ToString("0.000000", CultureInfo.InvariantCulture)}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "window":
                    session.State = ManageState.EditWindowStart;
                    await botClient.SendMessage(chatId, $"Window start local (HH:mm, current: {clubRun.WindowStartLocal}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "report":
                    session.State = ManageState.EditReportChat;
                    await botClient.SendMessage(chatId, $"Report chat id (current: {presentation.FormatReportChatId(clubRun.ReportChatId)}). Send 'empty' to clear or 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;
                case "check":
                    session.State = ManageState.EditCheckAt;
                    await botClient.SendMessage(chatId, $"Attendance check time local (HH:mm, current: {clubRun.CheckAtLocal}). Send 'skip' to cancel.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return;

                case "toggle":
                    var updatedRun = await repository.UpsertClubRunAsync(BuildClubRunUpsert(session.Draft, clubRun.Id, !clubRun.IsActive), ct);
                    session.Draft.Load(updatedRun);
                    await botClient.SendMessage(chatId, $"Run is now {(updatedRun.IsActive ? "active" : "inactive")}.\n\n{presentation.BuildEditMenuText(updatedRun)}", replyMarkup: presentation.BuildEditMenuMarkup(updatedRun.Id), cancellationToken: ct);
                    return;
                case "run":
                    var results = await services.GetRequiredService<AttendanceJobService>().RunAsync(null, clubRun.Id, chatId, skipExistingReports: false, ct);
                    await botClient.SendMessage(chatId, results.Count == 0 ? "Nothing to run." : "Run report posted to this chat.", replyMarkup: presentation.BuildEditMenuMarkup(clubRun.Id), cancellationToken: ct);
                    return;
                case "back":
                    session.State = ManageState.MainMenu;
                    session.SelectedClubId = null;
            session.SelectedClubRunId = null;
                    await botClient.SendMessage(chatId, "Manage club runs:", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                    return;
            }
        }

    }

    private async Task<bool> HandleCreateFlowAsync(long chatId, long telegramUserId, string text, ManageSession session, CancellationToken ct)
    {
        if (IsCancelText(text))
        {
            _sessions.TryRemove(telegramUserId, out _);
            await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return true;
        }

        switch (session.State)
        {
            case ManageState.CreateClubName:
                session.Draft.Name = text.Trim();
                if (string.IsNullOrWhiteSpace(session.Draft.Name))
                {
                    await botClient.SendMessage(chatId, "Club name cannot be empty.", cancellationToken: ct);
                    return true;
                }

                session.State = ManageState.CreateClubTimeZone;
                await botClient.SendMessage(chatId, "Club time zone id. Example: Asia/Tbilisi. Send 'skip' to use the default.", cancellationToken: ct);
                return true;
            case ManageState.CreateClubTimeZone:
                var clubName = session.Draft.Name;
                var defaultTimeZoneId = services.GetRequiredService<IOptions<AppOptions>>().Value.Schedule.TimeZoneId;
                var timeZoneId = IsSkipText(text) ? defaultTimeZoneId : text.Trim();
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                }
                catch
                {
                    await botClient.SendMessage(chatId, $"Unknown time zone id. Example: Asia/Tbilisi. Send 'skip' to use {defaultTimeZoneId}.", cancellationToken: ct);
                    return true;
                }

                var createdClub = await repository.UpsertClubAsync(null, clubName, Slugify(clubName), timeZoneId, true, ct);
                session.State = ManageState.MainMenu;
                session.SelectedClubId = createdClub.Id;
                await botClient.SendMessage(chatId, $"Club created: #{createdClub.Id} {createdClub.Name} ({createdClub.TimeZoneId})", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                return true;
            case ManageState.CreateSelectClub:
                await botClient.SendMessage(chatId, "Use the inline buttons below to choose a club.", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
                return true;
            case ManageState.CreateName:
                if (!session.SelectedClubId.HasValue)
                {
                    await SendClubSelectionAsync(chatId, "Choose a club for the new run:", "create", ct);
                    session.State = ManageState.CreateSelectClub;
                    return true;
                }

                session.Draft.ClubId = session.SelectedClubId.Value;
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
                session.State = ManageState.CreateCheckAt;
                await botClient.SendMessage(chatId, "Attendance check time local (HH:mm):", cancellationToken: ct);
                return true;
            case ManageState.CreateCheckAt:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.CheckAtLocal = text.Trim();
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
                _sessions[telegramUserId] = new ManageSession { State = ManageState.MainMenu };
                await botClient.SendMessage(
                    chatId,
                    $"Club run created.\n\n{presentation.BuildEditMenuText(created)}",
                    replyMarkup: presentation.BuildManageMainMenuMarkup(),
                    cancellationToken: ct);
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> HandleEditFlowAsync(long chatId, long telegramUserId, string text, ManageSession session, CancellationToken ct)
    {
        if (IsCancelText(text))
        {
            _sessions.TryRemove(telegramUserId, out _);
            await botClient.SendMessage(chatId, "Manage mode closed.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return true;
        }

        if (session.State == ManageState.EditSelectClub)
        {
            await SendClubSelectionAsync(chatId, "Choose a club to edit runs:", "edit", ct);
            return true;
        }

        if (session.State == ManageState.EditSelectRun)
        {
            var runs = await repository.GetClubRunsAsync(activeOnly: false, session.SelectedClubId, ct);
            await botClient.SendMessage(chatId, "Choose a club run to edit using the inline buttons below.", replyMarkup: presentation.BuildClubRunsInlineMarkup(runs), cancellationToken: ct);
            return true;
        }

        var selectedRun = session.SelectedClubRunId.HasValue ? await repository.GetClubRunByIdAsync(session.SelectedClubRunId.Value, ct) : null;
        if (selectedRun is null)
        {
            session.State = ManageState.EditSelectRun;
            var runs = await repository.GetClubRunsAsync(activeOnly: false, session.SelectedClubId, ct);
            await botClient.SendMessage(chatId, "Selected club run is missing. Choose a club run again.", replyMarkup: presentation.BuildClubRunsInlineMarkup(runs), cancellationToken: ct);
            return true;
        }

        if (session.State == ManageState.EditMenu)
        {
            await botClient.SendMessage(chatId, "Use the inline buttons below.", replyMarkup: presentation.BuildEditMenuMarkup(selectedRun.Id), cancellationToken: ct);
            return true;
        }

        if (IsSkipText(text) || IsBackText(text))
        {

            session.Draft.Load(selectedRun);
            session.State = ManageState.EditMenu;
            await botClient.SendMessage(chatId, presentation.BuildEditMenuText(selectedRun), replyMarkup: presentation.BuildEditMenuMarkup(selectedRun.Id), cancellationToken: ct);
            return true;
        }

        switch (session.State)
        {
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
                await botClient.SendMessage(chatId, $"Minute to (current: {selectedRun.MinuteTo}, >= {session.Draft.MinuteFrom}). Send 'skip' to cancel.", cancellationToken: ct);
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
                await botClient.SendMessage(chatId, $"Start longitude (current: {selectedRun.StartLng.ToString("0.000000", CultureInfo.InvariantCulture)}). Send 'skip' to cancel.", cancellationToken: ct);
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
                await botClient.SendMessage(chatId, $"Window end local (HH:mm, current: {selectedRun.WindowEndLocal}). Send 'skip' to cancel.", cancellationToken: ct);
                return true;
            case ManageState.EditWindowEnd:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.WindowEndLocal = text.Trim();
                session.State = ManageState.EditTargetStart;
                await botClient.SendMessage(chatId, $"Target start local (HH:mm, current: {selectedRun.TargetStartLocal}). Send 'skip' to cancel.", cancellationToken: ct);
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
            case ManageState.EditCheckAt:
                if (!IsValidTime(text))
                {
                    await botClient.SendMessage(chatId, "Use HH:mm format.", cancellationToken: ct);
                    return true;
                }
                session.Draft.CheckAtLocal = text.Trim();
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            case ManageState.EditReportChat:
                if (!text.Equals("empty", StringComparison.OrdinalIgnoreCase) && !long.TryParse(text, out var reportChatId))
                {
                    await botClient.SendMessage(chatId, "Enter numeric chat id, 'empty', or 'skip'.", cancellationToken: ct);
                    return true;
                }
                session.Draft.ReportChatId = text.Equals("empty", StringComparison.OrdinalIgnoreCase) ? null : long.Parse(text, CultureInfo.InvariantCulture);
                await SaveEditedRunAsync(chatId, session, selectedRun, ct);
                return true;
            default:
                return false;
        }
    }


    private async Task SaveEditedRunAsync(long chatId, ManageSession session, ClubRunRecord selectedRun, CancellationToken ct)
    {
        var updatedRun = await repository.UpsertClubRunAsync(BuildClubRunUpsert(session.Draft, selectedRun.Id, selectedRun.IsActive), ct);
        session.Draft.Load(updatedRun);
        session.State = ManageState.EditMenu;
        await botClient.SendMessage(chatId, $"Run updated.\n\n{presentation.BuildEditMenuText(updatedRun)}", replyMarkup: presentation.BuildEditMenuMarkup(updatedRun.Id), cancellationToken: ct);
    }

    private async Task SendClubRunsListAsync(long chatId, CancellationToken ct)
    {
        var runs = await repository.GetClubRunsAsync(activeOnly: false, ct: ct);
        var text = runs.Count == 0
            ? "No club runs created yet."
            : string.Join('\n', runs.Select(x => $"#{x.Id} {x.Name} | day={(DayOfWeek)x.DayOfWeek} | time={x.Hour:D2}:{x.MinuteFrom:D2} | active={x.IsActive}"));
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }
    private async Task HandleClubsAdminListAsync(long chatId, CancellationToken ct)
    {
        var clubs = await repository.GetClubsAsync(ct);
        var text = clubs.Count == 0
            ? "No clubs created yet."
            : string.Join('\n', clubs.Select(x => $"#{x.Id} {x.Name} | tz={x.TimeZoneId} | active={x.IsActive}"));
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task SendClubSelectionAsync(long chatId, string purpose, string mode, CancellationToken ct)
    {
        var clubs = await repository.GetClubsAsync(ct);
        if (clubs.Count == 0)
        {
            await botClient.SendMessage(chatId, "No clubs available. Create a club first.", replyMarkup: presentation.BuildManageMainMenuMarkup(), cancellationToken: ct);
            return;
        }

        await botClient.SendMessage(chatId, purpose, replyMarkup: presentation.BuildClubSelectionMarkup(clubs, mode), cancellationToken: ct);
    }

    private async Task SendClubRunsListAsync(long chatId, long? clubId, CancellationToken ct)
    {
        var runs = await repository.GetClubRunsAsync(activeOnly: false, clubId, ct);
        var text = runs.Count == 0
            ? "No runs created yet."
            : string.Join('\n', runs.Select(x => $"#{x.Id} {x.Name} | club={x.ClubId} | day={(DayOfWeek)x.DayOfWeek} | time={x.Hour:D2}:{x.MinuteFrom:D2} | active={x.IsActive}"));
        await botClient.SendMessage(chatId, text, cancellationToken: ct);
    }

    private async Task<bool> EnsureAdminOrReplyAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (await repository.IsAdminAsync(telegramUserId, ct))
        {
            return true;
        }

        await botClient.SendMessage(chatId, presentation.BuildNotAdminText(telegramUserId), cancellationToken: ct);
        return false;
    }

    private static bool IsValidTime(string value)
    {
        return TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _);
    }

    private static bool IsCancelText(string text)
    {
        return text.Equals("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackText(string text)
    {
        return text.Equals("back", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSkipText(string text)
    {
        return text.Equals("skip", StringComparison.OrdinalIgnoreCase);
    }

    private static ClubRunUpsert BuildClubRunUpsert(ClubRunDraft draft, long? id, bool isActive)
    {
        return new ClubRunUpsert(
            id,
            draft.ClubId,
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
            draft.CheckAtLocal,
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
}










