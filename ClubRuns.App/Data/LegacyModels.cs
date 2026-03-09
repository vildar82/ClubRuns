namespace ClubRuns.App.Data;

public sealed record LegacyStatUpsert(
    string DisplayName,
    string? TelegramUsername,
    int RunsCount,
    int SunCount,
    int NoSunCount,
    string SourceLine);

public sealed record LegacyStatRecord(
    string DisplayName,
    string? TelegramUsername,
    int RunsCount,
    int SunCount,
    int NoSunCount);

public sealed record AutoAttendanceAggregate(
    long TelegramUserId,
    string? TelegramUsername,
    string? FirstName,
    string? LastName,
    int SunCount,
    int NoSunCount);
