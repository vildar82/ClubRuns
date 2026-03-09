namespace ClubRuns.App.Services;

public sealed record RunStatisticsEntry(
    long ClubRunId,
    string ClubName,
    string RunName,
    int EventsCount,
    int TotalRegistered,
    int TotalFound,
    int TotalNotFound,
    int TotalErrors,
    int BestAttendance,
    string? BestAttendanceDate,
    string? LatestEventDate);

public sealed record LeaderboardSummary(
    IReadOnlyList<RunStatisticsEntry> Entries,
    int TotalRuns,
    int TotalEvents,
    int TotalRegistered,
    int TotalFound,
    int TotalNotFound,
    int TotalErrors);
