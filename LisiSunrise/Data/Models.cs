namespace LisiSunrise;

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

// attendance upsert payload for one user/date
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