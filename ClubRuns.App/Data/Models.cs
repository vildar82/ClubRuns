namespace ClubRuns.App.Data;

// users table row
public sealed record UserRecord(
    long Id,
    long TelegramUserId,
    string? TelegramUsername,
    string? FirstName,
    string? LastName,
    DateTimeOffset CreatedAt);

// strava_auth table row
public sealed record StravaAuthRecord(
    long UserId,
    long AthleteId,
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    string Scope,
    DateTimeOffset LastAuthAt);

// denormalized user + optional auth view used in app services
public sealed record UserWithAuth(UserRecord User, StravaAuthRecord? Auth);

// oauth_states table row
public sealed record OAuthStateRecord(string State, long TelegramUserId, DateTimeOffset CreatedAt);

// attendance upsert payload for the old single-run mode kept for legacy data compatibility
public sealed record AttendanceUpsert(
    string RunDate,
    long UserId,
    long? ActivityId,
    DateTimeOffset? StartDateLocal,
    double? StartLat,
    double? StartLng,
    double? DistanceKm,
    string MatchedReason);

// optional read model for future reporting queries
public sealed record AttendanceView(
    UserRecord User,
    long? ActivityId,
    string MatchedReason);

// club_runs table row
public sealed record ClubRunRecord(
    long Id,
    string Name,
    string Slug,
    bool IsActive,
    int DayOfWeek,
    int Hour,
    int MinuteFrom,
    int MinuteTo,
    double StartLat,
    double StartLng,
    double RadiusKm,
    string WindowStartLocal,
    string WindowEndLocal,
    string TargetStartLocal,
    string AllowedActivityTypes,
    double? MinDistanceKm,
    double? MaxDistanceKm,
    long? ReportChatId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// club_run_members table row
public sealed record ClubRunMemberRecord(
    long ClubRunId,
    long UserId,
    DateTimeOffset AddedAt);

// write model used by the bot management flow
public sealed record ClubRunUpsert(
    long? Id,
    string Name,
    string Slug,
    bool IsActive,
    int DayOfWeek,
    int Hour,
    int MinuteFrom,
    int MinuteTo,
    double StartLat,
    double StartLng,
    double RadiusKm,
    string WindowStartLocal,
    string WindowEndLocal,
    string TargetStartLocal,
    string AllowedActivityTypes,
    double? MinDistanceKm,
    double? MaxDistanceKm,
    long? ReportChatId);

// club_run_reports table row
public sealed record ClubRunReportRecord(
    long ClubRunId,
    string RunDate,
    DateTimeOffset GeneratedAt,
    int TotalUsers,
    int FoundCount);

// club_run_attendance upsert payload
public sealed record ClubRunAttendanceUpsert(
    long ClubRunId,
    string RunDate,
    long UserId,
    long? ActivityId,
    DateTimeOffset? StartDateLocal,
    double? StartLat,
    double? StartLng,
    double? DistanceKm,
    string MatchedReason);
