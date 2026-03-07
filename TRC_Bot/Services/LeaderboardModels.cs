namespace TRC_Bot;

public sealed record LeaderboardEntry(
    string DisplayName,
    string? TelegramUsername,
    int RunsCount,
    int SunCount,
    int NoSunCount);

public sealed record LeaderboardSummary(
    IReadOnlyList<LeaderboardEntry> Entries,
    int TotalParticipants,
    int TotalFoundAttendances,
    int TotalMissedAttendances,
    int TotalTrackedAttempts,
    int AutoRunsCount,
    int? LatestRunAttendance,
    int? HighestAttendance,
    string? HighestAttendanceDate);
