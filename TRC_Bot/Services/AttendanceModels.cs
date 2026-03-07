namespace TRC_Bot;

public sealed record AttendanceUserResult(
    UserRecord User,
    bool Found,
    long? ActivityId,
    string Reason,
    bool IsError,
    string? ErrorMessage);

public sealed record AttendanceRunResult(
    string RunDate,
    List<AttendanceUserResult> Found,
    List<AttendanceUserResult> NotFound,
    List<AttendanceUserResult> Errors);
