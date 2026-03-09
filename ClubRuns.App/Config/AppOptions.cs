namespace ClubRuns.App.Config;

public sealed class AppOptions
{
    // Root strongly-typed configuration object bound from appsettings + env vars.
    public TelegramOptions Telegram { get; init; } = new();
    public StravaOptions Strava { get; init; } = new();
    public ScheduleOptions Schedule { get; init; } = new();
    public DatabaseOptions Database { get; init; } = new();
    public LoggingOptions Logging { get; init; } = new();
}

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = string.Empty;
    public long[] AdminTelegramUserIds { get; init; } = [];
}

public sealed class StravaOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public bool UseReadAllScope { get; init; }
}

public sealed class DatabaseOptions
{
    public string Path { get; init; } = "trc_bot.db";
}

public sealed class ScheduleOptions
{
    // Global scheduler window in Tbilisi local time.
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Friday;
    public int Hour { get; init; } = 12;
    public int MinuteFrom { get; init; } = 0;
    public int MinuteTo { get; init; } = 10;
}

public sealed class LoggingOptions
{
    public string LogPath { get; init; } = "logs/trc-bot-.log";
}
