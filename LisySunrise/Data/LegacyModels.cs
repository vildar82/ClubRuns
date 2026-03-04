namespace LisySunrise.Data;

public sealed record LegacyStatUpsert(
    string DisplayName,
    string? TelegramUsername,
    int RunsCount,
    int SunCount,
    int NoSunCount,
    string SourceLine);
