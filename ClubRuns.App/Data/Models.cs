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

// Current recurring run template used by the bot UI.
public sealed record ClubRunRecord(
    long Id,
    long ClubId,
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
    string CheckAtLocal,
    string AllowedActivityTypes,
    double? MinDistanceKm,
    double? MaxDistanceKm,
    long? ReportChatId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);


public sealed record ClubRunUpsert(
    long? Id,
    long ClubId,
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
    string CheckAtLocal,
    string AllowedActivityTypes,
    double? MinDistanceKm,
    double? MaxDistanceKm,
    long? ReportChatId);

public sealed record ClubRecord(
    long Id,
    string Slug,
    string Name,
    string TimeZoneId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EventInstanceRecord(
    long Id,
    long ClubRunId,
    string EventDateLocal,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record EventRegistrationRecord(
    long EventInstanceId,
    long UserId,
    string Status,
    DateTimeOffset RegisteredAtUtc,
    string Source);

public sealed record EventResultUpsert(
    long EventInstanceId,
    long UserId,
    string Status,
    long? StravaActivityId,
    DateTimeOffset? MatchedAtUtc,
    DateTimeOffset? ActivityStartUtc,
    string? ActivityStartLocal,
    double? StartLat,
    double? StartLng,
    double? DistanceKm,
    string? MatchedReason,
    string? ErrorMessage);