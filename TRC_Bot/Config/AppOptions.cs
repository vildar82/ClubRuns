namespace TRC_Bot;

public sealed class AppOptions
{
    // Root strongly-typed configuration object bound from appsettings + env vars.
    public TelegramOptions Telegram { get; init; } = new();
    public StravaOptions Strava { get; init; } = new();
    public MatchingOptions Matching { get; init; } = new();
    public ScheduleOptions Schedule { get; init; } = new();
    public DatabaseOptions Database { get; init; } = new();
    public LoggingOptions Logging { get; init; } = new();
}

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = string.Empty;
    public long? GroupChatId { get; init; }
    public long[] AdminTelegramUserIds { get; init; } = [];
}

public sealed class StravaOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public bool UseReadAllScope { get; init; }
}

public sealed class MatchingOptions
{
    // Lisi start reference point and filters for attendance matching.
    public double LisiStartLat { get; init; }
    public double LisiStartLng { get; init; }
    public double RadiusKm { get; init; }
    public string WindowStartLocal { get; init; } = "06:00";
    public string WindowEndLocal { get; init; } = "10:00";
    public string TargetStartLocal { get; init; } = "08:00";
    public string[] AllowedActivityTypes { get; init; } = ["Run", "TrailRun"];
    public double? MinDistanceKm { get; init; }
    public double? MaxDistanceKm { get; init; }
}

public sealed class DatabaseOptions
{
    public string Path { get; init; } = "lisi_sunrise.db";
}

public sealed class ScheduleOptions
{
    // Attendance auto-run schedule in Tbilisi local time.
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Friday;
    public int Hour { get; init; } = 12;
    public int MinuteFrom { get; init; } = 0;
    public int MinuteTo { get; init; } = 10;
}

public sealed class LoggingOptions
{
    public string LogPath { get; init; } = "logs/lisi-sunrise-.log";
}
