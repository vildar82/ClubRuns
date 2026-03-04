namespace LisiSunrise;

public sealed record LeaderboardEntry(
    string DisplayName,
    string? TelegramUsername,
    int RunsCount,
    int SunCount,
    int NoSunCount);

public sealed record LeaderboardSummary(
    IReadOnlyList<LeaderboardEntry> Entries,
    int TotalSunrisers,
    int TotalSunriseExperiences,
    int TotalNoSunExperiences,
    int TotalOutOfBed,
    int AutoRunsCount,
    int? LatestRunAttendance,
    int? HighestAttendance,
    string? HighestAttendanceDate);
