using ClubRuns.App.Data;

namespace ClubRuns.App.Services;

public sealed record AttendanceUserResult(
    UserRecord User,
    bool Found,
    long? ActivityId,
    string Reason,
    bool IsError,
    string? ErrorMessage);

public sealed record ClubRunAttendanceResult(
    ClubRunRecord ClubRun,
    string RunDate,
    List<AttendanceUserResult> Found,
    List<AttendanceUserResult> NotFound,
    List<AttendanceUserResult> Errors);
