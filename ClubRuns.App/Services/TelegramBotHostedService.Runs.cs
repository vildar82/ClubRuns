using System.Globalization;
using ClubRuns.App.Data;
using Telegram.Bot;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService
{
    private async Task HandleRunsCallbackAsync(long chatId, long telegramUserId, string data, CancellationToken ct)
    {
        await EnsureTelegramUserExistsAsync(telegramUserId, ct);

        if (data == "runs:list")
        {
            await ShowRunsMenuAsync(chatId, telegramUserId, ct);
            return;
        }

        if (data == "runs:mine")
        {
            await ShowMyRegistrationsAsync(chatId, telegramUserId, ct);
            return;
        }

        if (!data.StartsWith("runs:", StringComparison.Ordinal))
        {
            return;
        }

        var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !long.TryParse(parts[2], out var clubRunId))
        {
            return;
        }

        var clubRun = await repository.GetClubRunByIdAsync(clubRunId, ct);
        if (clubRun is null || !clubRun.IsActive)
        {
            await botClient.SendMessage(chatId, "Run not found.", cancellationToken: ct);
            return;
        }

        var user = await repository.GetUserByTelegramIdAsync(telegramUserId, ct);
        if (user is null)
        {
            await botClient.SendMessage(chatId, "Run /start first.", cancellationToken: ct);
            return;
        }

        var eventInstance = await EnsureUpcomingEventInstanceAsync(clubRun, ct);

        switch (parts[1])
        {
            case "view":
                await ShowRunDetailsAsync(chatId, user.Id, clubRun, eventInstance, ct);
                return;
            case "join":
                await repository.UpsertEventRegistrationAsync(eventInstance.Id, user.Id, "registered", "telegram", ct);
                await ShowRunDetailsAsync(chatId, user.Id, clubRun, eventInstance, ct, "Registered.");
                return;
            case "leave":
                await repository.UpsertEventRegistrationAsync(eventInstance.Id, user.Id, "cancelled", "telegram", ct);
                await ShowRunDetailsAsync(chatId, user.Id, clubRun, eventInstance, ct, "Registration cancelled.");
                return;
        }
    }

    private async Task ShowRunDetailsAsync(long chatId, long userId, ClubRunRecord clubRun, EventInstanceRecord eventInstance, CancellationToken ct, string? prefix = null)
    {
        var isRegistered = await repository.IsEventRegistrationActiveAsync(eventInstance.Id, userId, ct);
        var club = await repository.GetClubByIdAsync(clubRun.ClubId, ct);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            lines.Add(prefix);
            lines.Add(string.Empty);
        }

        lines.Add(club is null ? clubRun.Name : $"{club.Name} / {clubRun.Name}");
        lines.Add($"Event date: {eventInstance.EventDateLocal}");
        lines.Add($"Schedule: {(DayOfWeek)clubRun.DayOfWeek} {clubRun.Hour:D2}:{clubRun.MinuteFrom:D2}-{clubRun.Hour:D2}:{clubRun.MinuteTo:D2}");
        lines.Add($"Place: {clubRun.StartLat.ToString("0.000000", CultureInfo.InvariantCulture)}, {clubRun.StartLng.ToString("0.000000", CultureInfo.InvariantCulture)}");
        lines.Add($"Radius: {clubRun.RadiusKm.ToString("0.##", CultureInfo.InvariantCulture)} km");
        lines.Add($"Registration: {(isRegistered ? "registered" : "not registered")}");

        await botClient.SendMessage(chatId, string.Join(Environment.NewLine, lines), replyMarkup: presentation.BuildRunDetailsMarkup(clubRun.Id, isRegistered), cancellationToken: ct);
    }

    private async Task<EventInstanceRecord> EnsureUpcomingEventInstanceAsync(ClubRunRecord clubRun, CancellationToken ct)
    {
        var timeZone = ResolveConfiguredTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var targetDate = GetUpcomingEventDate(clubRun, nowLocal.DateTime);
        var runDate = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var eventStartUtc = ToUtc(targetDate, new TimeSpan(clubRun.Hour, clubRun.MinuteFrom, 0), timeZone);
        var windowStartUtc = ToUtc(targetDate, TimeSpan.ParseExact(clubRun.WindowStartLocal, @"hh\:mm", CultureInfo.InvariantCulture), timeZone);
        var windowEndUtc = ToUtc(targetDate, TimeSpan.ParseExact(clubRun.WindowEndLocal, @"hh\:mm", CultureInfo.InvariantCulture), timeZone);
        return await repository.EnsureEventInstanceAsync(clubRun.Id, runDate, eventStartUtc, windowStartUtc, windowEndUtc, ct);
    }

    private TimeZoneInfo ResolveConfiguredTimeZone()
    {
        var timeZoneId = options.Value.Schedule.TimeZoneId;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tbilisi");
        }
    }

    private static DateOnly GetUpcomingEventDate(ClubRunRecord clubRun, DateTime nowLocal)
    {
        var current = DateOnly.FromDateTime(nowLocal);
        var dayDiff = ((clubRun.DayOfWeek - (int)nowLocal.DayOfWeek) + 7) % 7;
        var target = current.AddDays(dayDiff);
        var targetStart = target.ToDateTime(new TimeOnly(clubRun.Hour, clubRun.MinuteFrom));
        if (dayDiff == 0 && targetStart < nowLocal)
        {
            target = target.AddDays(7);
        }

        return target;
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeSpan localTime, TimeZoneInfo timeZone)
    {
        var localDateTime = date.ToDateTime(TimeOnly.MinValue).Add(localTime);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}


