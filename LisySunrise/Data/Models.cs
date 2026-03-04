namespace LisySunrise.Data;

public sealed record UserRecord(
    long Id,
    long TelegramUserId,
    string? TelegramUsername,
    string? FirstName,
    string? LastName,
    DateTimeOffset CreatedAt);

public sealed record StravaAuthRecord(
    long UserId,
    long AthleteId,
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    string Scope,
    DateTimeOffset LastAuthAt);

public sealed record UserWithAuth(UserRecord User, StravaAuthRecord? Auth);

public sealed record OAuthStateRecord(string State, long TelegramUserId, DateTimeOffset CreatedAt);

public sealed record AttendanceUpsert(
    string RunDate,
    long UserId,
    long? ActivityId,
    DateTimeOffset? StartDateLocal,
    double? StartLat,
    double? StartLng,
    double? DistanceKm,
    string MatchedReason);

public sealed record AttendanceView(
    UserRecord User,
    long? ActivityId,
    string MatchedReason);
