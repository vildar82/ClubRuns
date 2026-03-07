using Microsoft.Data.Sqlite;

namespace TRC_Bot;

public sealed class SqliteRepository
{
    private readonly string _connectionString;
    private readonly ITokenProtector _tokenProtector;

    public SqliteRepository(string dbPath, ITokenProtector tokenProtector)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _tokenProtector = tokenProtector;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    telegram_user_id INTEGER NOT NULL UNIQUE,
    telegram_username TEXT,
    first_name TEXT,
    last_name TEXT,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS strava_auth (
    user_id INTEGER PRIMARY KEY,
    athlete_id INTEGER NOT NULL,
    access_token TEXT NOT NULL,
    refresh_token TEXT NOT NULL,
    expires_at INTEGER NOT NULL,
    scope TEXT NOT NULL,
    last_auth_at INTEGER NOT NULL,
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS runs (
    date TEXT PRIMARY KEY,
    generated_at INTEGER NOT NULL,
    total_users INTEGER NOT NULL,
    found_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS attendance (
    run_date TEXT NOT NULL,
    user_id INTEGER NOT NULL,
    activity_id INTEGER,
    start_date_local INTEGER,
    start_lat REAL,
    start_lng REAL,
    distance_km REAL,
    matched_reason TEXT NOT NULL,
    PRIMARY KEY (run_date, user_id),
    FOREIGN KEY(run_date) REFERENCES runs(date),
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS oauth_states (
    state TEXT PRIMARY KEY,
    telegram_user_id INTEGER NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS legacy_stats (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT NOT NULL,
    telegram_username TEXT,
    runs_count INTEGER NOT NULL,
    sun_count INTEGER NOT NULL,
    nosun_count INTEGER NOT NULL,
    source_line TEXT NOT NULL,
    imported_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS admins (
    telegram_user_id INTEGER PRIMARY KEY,
    added_by_telegram_user_id INTEGER,
    added_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS app_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS club_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    is_active INTEGER NOT NULL,
    day_of_week INTEGER NOT NULL,
    hour INTEGER NOT NULL,
    minute_from INTEGER NOT NULL,
    minute_to INTEGER NOT NULL,
    start_lat REAL NOT NULL,
    start_lng REAL NOT NULL,
    radius_km REAL NOT NULL,
    window_start_local TEXT NOT NULL,
    window_end_local TEXT NOT NULL,
    target_start_local TEXT NOT NULL,
    allowed_activity_types TEXT NOT NULL,
    min_distance_km REAL,
    max_distance_km REAL,
    report_chat_id INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS club_run_members (
    club_run_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    added_at INTEGER NOT NULL,
    PRIMARY KEY (club_run_id, user_id),
    FOREIGN KEY(club_run_id) REFERENCES club_runs(id),
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS club_run_reports (
    club_run_id INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    generated_at INTEGER NOT NULL,
    total_users INTEGER NOT NULL,
    found_count INTEGER NOT NULL,
    PRIMARY KEY (club_run_id, run_date),
    FOREIGN KEY(club_run_id) REFERENCES club_runs(id)
);

CREATE TABLE IF NOT EXISTS club_run_attendance (
    club_run_id INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    user_id INTEGER NOT NULL,
    activity_id INTEGER,
    start_date_local INTEGER,
    start_lat REAL,
    start_lng REAL,
    distance_km REAL,
    matched_reason TEXT NOT NULL,
    PRIMARY KEY (club_run_id, run_date, user_id),
    FOREIGN KEY(club_run_id, run_date) REFERENCES club_run_reports(club_run_id, run_date),
    FOREIGN KEY(user_id) REFERENCES users(id)
);
";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<UserRecord> UpsertUserAsync(long telegramUserId, string? username, string? firstName, string? lastName, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var upsert = connection.CreateCommand();
        upsert.CommandText = @"
INSERT INTO users (telegram_user_id, telegram_username, first_name, last_name, created_at)
VALUES ($telegram_user_id, $telegram_username, $first_name, $last_name, $created_at)
ON CONFLICT(telegram_user_id) DO UPDATE SET
    telegram_username = excluded.telegram_username,
    first_name = excluded.first_name,
    last_name = excluded.last_name;";
        upsert.Parameters.AddWithValue("$telegram_user_id", telegramUserId);
        upsert.Parameters.AddWithValue("$telegram_username", (object?)username ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$first_name", (object?)firstName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$last_name", (object?)lastName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$created_at", now);
        await upsert.ExecuteNonQueryAsync(ct);

        return (await GetUserByTelegramIdAsync(telegramUserId, ct))!;
    }

    public async Task<UserRecord?> GetUserByTelegramIdAsync(long telegramUserId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, telegram_user_id, telegram_username, first_name, last_name, created_at
FROM users
WHERE telegram_user_id = $telegram_user_id;";
        cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapUser(reader);
    }

    public async Task<UserRecord?> GetUserByIdAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, telegram_user_id, telegram_username, first_name, last_name, created_at
FROM users
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapUser(reader);
    }

    public async Task<bool> IsStravaConnectedByTelegramUserIdAsync(long telegramUserId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT 1
FROM users u
JOIN strava_auth s ON s.user_id = u.id
WHERE u.telegram_user_id = $telegram_user_id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task<UserRecord?> GetUserByTelegramUsernameAsync(string telegramUsername, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, telegram_user_id, telegram_username, first_name, last_name, created_at
FROM users
WHERE telegram_username IS NOT NULL AND lower(telegram_username) = lower($telegram_username)
LIMIT 1;";
        cmd.Parameters.AddWithValue("$telegram_username", telegramUsername);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapUser(reader);
    }

    public async Task<List<UserWithAuth>> GetAllUsersWithAuthAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
    u.id, u.telegram_user_id, u.telegram_username, u.first_name, u.last_name, u.created_at,
    s.user_id, s.athlete_id, s.access_token, s.refresh_token, s.expires_at, s.scope, s.last_auth_at
FROM users u
LEFT JOIN strava_auth s ON s.user_id = u.id
ORDER BY u.id;";

        return await ReadUsersWithAuthAsync(cmd, ct);
    }

    public async Task<List<UserWithAuth>> GetClubRunMembersWithAuthAsync(long clubRunId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
    u.id, u.telegram_user_id, u.telegram_username, u.first_name, u.last_name, u.created_at,
    s.user_id, s.athlete_id, s.access_token, s.refresh_token, s.expires_at, s.scope, s.last_auth_at
FROM club_run_members m
JOIN users u ON u.id = m.user_id
LEFT JOIN strava_auth s ON s.user_id = u.id
WHERE m.club_run_id = $club_run_id
ORDER BY u.telegram_username, u.first_name, u.last_name, u.id;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);

        return await ReadUsersWithAuthAsync(cmd, ct);
    }

    public async Task SaveStravaAuthAsync(long userId, long athleteId, string accessToken, string refreshToken, long expiresAt, string scope, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var protectedAccessToken = _tokenProtector.Protect(accessToken);
        var protectedRefreshToken = _tokenProtector.Protect(refreshToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO strava_auth (user_id, athlete_id, access_token, refresh_token, expires_at, scope, last_auth_at)
VALUES ($user_id, $athlete_id, $access_token, $refresh_token, $expires_at, $scope, $last_auth_at)
ON CONFLICT(user_id) DO UPDATE SET
    athlete_id = excluded.athlete_id,
    access_token = excluded.access_token,
    refresh_token = excluded.refresh_token,
    expires_at = excluded.expires_at,
    scope = excluded.scope,
    last_auth_at = excluded.last_auth_at;";
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$athlete_id", athleteId);
        cmd.Parameters.AddWithValue("$access_token", protectedAccessToken);
        cmd.Parameters.AddWithValue("$refresh_token", protectedRefreshToken);
        cmd.Parameters.AddWithValue("$expires_at", expiresAt);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$last_auth_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveOAuthStateAsync(string state, long telegramUserId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO oauth_states (state, telegram_user_id, created_at)
VALUES ($state, $telegram_user_id, $created_at)
ON CONFLICT(state) DO UPDATE SET
    telegram_user_id = excluded.telegram_user_id,
    created_at = excluded.created_at;";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);
        cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OAuthStateRecord?> ConsumeOAuthStateAsync(string state, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        OAuthStateRecord? result = null;

        await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct))
        {
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT state, telegram_user_id, created_at FROM oauth_states WHERE state = $state;";
                select.Parameters.AddWithValue("$state", state);

                await using var reader = await select.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    result = new OAuthStateRecord(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
                }
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM oauth_states WHERE state = $state;";
            delete.Parameters.AddWithValue("$state", state);
            await delete.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }

        return result;
    }

    public async Task SaveRunAsync(string runDate, int totalUsers, int foundCount, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO runs (date, generated_at, total_users, found_count)
VALUES ($date, $generated_at, $total_users, $found_count)
ON CONFLICT(date) DO UPDATE SET
    generated_at = excluded.generated_at,
    total_users = excluded.total_users,
    found_count = excluded.found_count;";
        cmd.Parameters.AddWithValue("$date", runDate);
        cmd.Parameters.AddWithValue("$generated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$total_users", totalUsers);
        cmd.Parameters.AddWithValue("$found_count", foundCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAttendanceAsync(AttendanceUpsert attendance, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO attendance (run_date, user_id, activity_id, start_date_local, start_lat, start_lng, distance_km, matched_reason)
VALUES ($run_date, $user_id, $activity_id, $start_date_local, $start_lat, $start_lng, $distance_km, $matched_reason)
ON CONFLICT(run_date, user_id) DO UPDATE SET
    activity_id = excluded.activity_id,
    start_date_local = excluded.start_date_local,
    start_lat = excluded.start_lat,
    start_lng = excluded.start_lng,
    distance_km = excluded.distance_km,
    matched_reason = excluded.matched_reason;";
        cmd.Parameters.AddWithValue("$run_date", attendance.RunDate);
        cmd.Parameters.AddWithValue("$user_id", attendance.UserId);
        cmd.Parameters.AddWithValue("$activity_id", (object?)attendance.ActivityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_date_local", attendance.StartDateLocal.HasValue ? attendance.StartDateLocal.Value.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lat", (object?)attendance.StartLat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lng", (object?)attendance.StartLng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$distance_km", (object?)attendance.DistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$matched_reason", attendance.MatchedReason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RunExistsAsync(string runDate, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM runs WHERE date = $date LIMIT 1;";
        cmd.Parameters.AddWithValue("$date", runDate);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task<(int total, int connected)> GetUserStatsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM users),
  (SELECT COUNT(*) FROM strava_auth);";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task ReplaceLegacyStatsAsync(IReadOnlyCollection<LegacyStatUpsert> stats, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM legacy_stats;";
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var item in stats)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
INSERT INTO legacy_stats (display_name, telegram_username, runs_count, sun_count, nosun_count, source_line, imported_at)
VALUES ($display_name, $telegram_username, $runs_count, $sun_count, $nosun_count, $source_line, $imported_at);";
            insert.Parameters.AddWithValue("$display_name", item.DisplayName);
            insert.Parameters.AddWithValue("$telegram_username", (object?)item.TelegramUsername ?? DBNull.Value);
            insert.Parameters.AddWithValue("$runs_count", item.RunsCount);
            insert.Parameters.AddWithValue("$sun_count", item.SunCount);
            insert.Parameters.AddWithValue("$nosun_count", item.NoSunCount);
            insert.Parameters.AddWithValue("$source_line", item.SourceLine);
            insert.Parameters.AddWithValue("$imported_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<List<LegacyStatRecord>> GetLegacyStatsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT display_name, telegram_username, runs_count, sun_count, nosun_count
FROM legacy_stats;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<LegacyStatRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LegacyStatRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return list;
    }

    public async Task<List<AutoAttendanceAggregate>> GetAutoAttendanceAggregatesAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
    u.telegram_user_id,
    u.telegram_username,
    u.first_name,
    u.last_name,
    SUM(CASE WHEN a.activity_id IS NOT NULL THEN 1 ELSE 0 END) AS sun_count,
    SUM(CASE WHEN a.activity_id IS NULL AND a.matched_reason NOT LIKE 'error:%' THEN 1 ELSE 0 END) AS nosun_count
FROM attendance a
JOIN users u ON u.id = a.user_id
LEFT JOIN strava_auth sa ON sa.user_id = u.id
WHERE sa.user_id IS NOT NULL
GROUP BY u.telegram_user_id, u.telegram_username, u.first_name, u.last_name;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AutoAttendanceAggregate>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AutoAttendanceAggregate(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }

        return list;
    }

    public async Task<(int totalRuns, int? latestAttendance, int? highestAttendance, string? highestDate)> GetRunsSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalRuns = 0;
        int? latestAttendance = null;
        int? highestAttendance = null;
        string? highestDate = null;

        await using (var totalCmd = connection.CreateCommand())
        {
            totalCmd.CommandText = "SELECT COUNT(*) FROM runs;";
            totalRuns = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct) ?? 0);
        }

        await using (var latestCmd = connection.CreateCommand())
        {
            latestCmd.CommandText = "SELECT found_count FROM runs ORDER BY date DESC LIMIT 1;";
            var value = await latestCmd.ExecuteScalarAsync(ct);
            if (value is not null && value is not DBNull)
            {
                latestAttendance = Convert.ToInt32(value);
            }
        }

        await using (var highestCmd = connection.CreateCommand())
        {
            highestCmd.CommandText = "SELECT found_count, date FROM runs ORDER BY found_count DESC, date ASC LIMIT 1;";
            await using var reader = await highestCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                highestAttendance = reader.GetInt32(0);
                highestDate = reader.GetString(1);
            }
        }

        return (totalRuns, latestAttendance, highestAttendance, highestDate);
    }

    public async Task EnsureAdminsAsync(IEnumerable<long> telegramUserIds, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        foreach (var telegramUserId in telegramUserIds.Distinct())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO admins (telegram_user_id, added_by_telegram_user_id, added_at)
VALUES ($telegram_user_id, NULL, $added_at)
ON CONFLICT(telegram_user_id) DO NOTHING;";
            cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);
            cmd.Parameters.AddWithValue("$added_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> IsAdminAsync(long telegramUserId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM admins WHERE telegram_user_id = $telegram_user_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task AddAdminAsync(long telegramUserId, long addedByTelegramUserId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO admins (telegram_user_id, added_by_telegram_user_id, added_at)
VALUES ($telegram_user_id, $added_by, $added_at)
ON CONFLICT(telegram_user_id) DO UPDATE SET
    added_by_telegram_user_id = excluded.added_by_telegram_user_id,
    added_at = excluded.added_at;";
        cmd.Parameters.AddWithValue("$telegram_user_id", telegramUserId);
        cmd.Parameters.AddWithValue("$added_by", addedByTelegramUserId);
        cmd.Parameters.AddWithValue("$added_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<long>> GetAdminTelegramUserIdsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT telegram_user_id FROM admins ORDER BY telegram_user_id;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    public async Task UpsertSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO app_settings (key, value, updated_at)
VALUES ($key, $value, $updated_at)
ON CONFLICT(key) DO UPDATE SET
    value = excluded.value,
    updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ClubRunRecord>> GetClubRunsAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, slug, is_active, day_of_week, hour, minute_from, minute_to,
       start_lat, start_lng, radius_km, window_start_local, window_end_local, target_start_local,
       allowed_activity_types, min_distance_km, max_distance_km, report_chat_id, created_at, updated_at
FROM club_runs
WHERE $active_only = 0 OR is_active = 1
ORDER BY day_of_week, hour, minute_from, name;";
        cmd.Parameters.AddWithValue("$active_only", activeOnly ? 1 : 0);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ClubRunRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapClubRun(reader));
        }

        return list;
    }

    public async Task<List<ClubRunRecord>> GetClubRunsForDayAsync(DayOfWeek dayOfWeek, bool activeOnly, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, slug, is_active, day_of_week, hour, minute_from, minute_to,
       start_lat, start_lng, radius_km, window_start_local, window_end_local, target_start_local,
       allowed_activity_types, min_distance_km, max_distance_km, report_chat_id, created_at, updated_at
FROM club_runs
WHERE day_of_week = $day_of_week AND ($active_only = 0 OR is_active = 1)
ORDER BY hour, minute_from, name;";
        cmd.Parameters.AddWithValue("$day_of_week", (int)dayOfWeek);
        cmd.Parameters.AddWithValue("$active_only", activeOnly ? 1 : 0);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ClubRunRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapClubRun(reader));
        }

        return list;
    }

    public async Task<ClubRunRecord?> GetClubRunByIdAsync(long clubRunId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, slug, is_active, day_of_week, hour, minute_from, minute_to,
       start_lat, start_lng, radius_km, window_start_local, window_end_local, target_start_local,
       allowed_activity_types, min_distance_km, max_distance_km, report_chat_id, created_at, updated_at
FROM club_runs
WHERE id = $id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", clubRunId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapClubRun(reader);
    }

    public async Task<ClubRunRecord> UpsertClubRunAsync(ClubRunUpsert upsert, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO club_runs (
    id, name, slug, is_active, day_of_week, hour, minute_from, minute_to,
    start_lat, start_lng, radius_km, window_start_local, window_end_local, target_start_local,
    allowed_activity_types, min_distance_km, max_distance_km, report_chat_id, created_at, updated_at)
VALUES (
    $id, $name, $slug, $is_active, $day_of_week, $hour, $minute_from, $minute_to,
    $start_lat, $start_lng, $radius_km, $window_start_local, $window_end_local, $target_start_local,
    $allowed_activity_types, $min_distance_km, $max_distance_km, $report_chat_id, COALESCE($created_at, $updated_at), $updated_at)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    slug = excluded.slug,
    is_active = excluded.is_active,
    day_of_week = excluded.day_of_week,
    hour = excluded.hour,
    minute_from = excluded.minute_from,
    minute_to = excluded.minute_to,
    start_lat = excluded.start_lat,
    start_lng = excluded.start_lng,
    radius_km = excluded.radius_km,
    window_start_local = excluded.window_start_local,
    window_end_local = excluded.window_end_local,
    target_start_local = excluded.target_start_local,
    allowed_activity_types = excluded.allowed_activity_types,
    min_distance_km = excluded.min_distance_km,
    max_distance_km = excluded.max_distance_km,
    report_chat_id = excluded.report_chat_id,
    updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$id", (object?)upsert.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", upsert.Name);
        cmd.Parameters.AddWithValue("$slug", upsert.Slug);
        cmd.Parameters.AddWithValue("$is_active", upsert.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$day_of_week", upsert.DayOfWeek);
        cmd.Parameters.AddWithValue("$hour", upsert.Hour);
        cmd.Parameters.AddWithValue("$minute_from", upsert.MinuteFrom);
        cmd.Parameters.AddWithValue("$minute_to", upsert.MinuteTo);
        cmd.Parameters.AddWithValue("$start_lat", upsert.StartLat);
        cmd.Parameters.AddWithValue("$start_lng", upsert.StartLng);
        cmd.Parameters.AddWithValue("$radius_km", upsert.RadiusKm);
        cmd.Parameters.AddWithValue("$window_start_local", upsert.WindowStartLocal);
        cmd.Parameters.AddWithValue("$window_end_local", upsert.WindowEndLocal);
        cmd.Parameters.AddWithValue("$target_start_local", upsert.TargetStartLocal);
        cmd.Parameters.AddWithValue("$allowed_activity_types", upsert.AllowedActivityTypes);
        cmd.Parameters.AddWithValue("$min_distance_km", (object?)upsert.MinDistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max_distance_km", (object?)upsert.MaxDistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$report_chat_id", (object?)upsert.ReportChatId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", upsert.Id.HasValue ? DBNull.Value : now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        await cmd.ExecuteNonQueryAsync(ct);

        if (upsert.Id.HasValue)
        {
            return (await GetClubRunByIdAsync(upsert.Id.Value, ct))!;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = @"
SELECT id, name, slug, is_active, day_of_week, hour, minute_from, minute_to,
       start_lat, start_lng, radius_km, window_start_local, window_end_local, target_start_local,
       allowed_activity_types, min_distance_km, max_distance_km, report_chat_id, created_at, updated_at
FROM club_runs
WHERE slug = $slug
LIMIT 1;";
        select.Parameters.AddWithValue("$slug", upsert.Slug);

        await using var reader = await select.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapClubRun(reader);
    }

    public async Task AddClubRunMemberAsync(long clubRunId, long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO club_run_members (club_run_id, user_id, added_at)
VALUES ($club_run_id, $user_id, $added_at)
ON CONFLICT(club_run_id, user_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$added_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveClubRunMemberAsync(long clubRunId, long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM club_run_members WHERE club_run_id = $club_run_id AND user_id = $user_id;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$user_id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsClubRunMemberAsync(long clubRunId, long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM club_run_members WHERE club_run_id = $club_run_id AND user_id = $user_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$user_id", userId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task SaveClubRunReportAsync(long clubRunId, string runDate, int totalUsers, int foundCount, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO club_run_reports (club_run_id, run_date, generated_at, total_users, found_count)
VALUES ($club_run_id, $run_date, $generated_at, $total_users, $found_count)
ON CONFLICT(club_run_id, run_date) DO UPDATE SET
    generated_at = excluded.generated_at,
    total_users = excluded.total_users,
    found_count = excluded.found_count;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$run_date", runDate);
        cmd.Parameters.AddWithValue("$generated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$total_users", totalUsers);
        cmd.Parameters.AddWithValue("$found_count", foundCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertClubRunAttendanceAsync(ClubRunAttendanceUpsert attendance, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO club_run_attendance (club_run_id, run_date, user_id, activity_id, start_date_local, start_lat, start_lng, distance_km, matched_reason)
VALUES ($club_run_id, $run_date, $user_id, $activity_id, $start_date_local, $start_lat, $start_lng, $distance_km, $matched_reason)
ON CONFLICT(club_run_id, run_date, user_id) DO UPDATE SET
    activity_id = excluded.activity_id,
    start_date_local = excluded.start_date_local,
    start_lat = excluded.start_lat,
    start_lng = excluded.start_lng,
    distance_km = excluded.distance_km,
    matched_reason = excluded.matched_reason;";
        cmd.Parameters.AddWithValue("$club_run_id", attendance.ClubRunId);
        cmd.Parameters.AddWithValue("$run_date", attendance.RunDate);
        cmd.Parameters.AddWithValue("$user_id", attendance.UserId);
        cmd.Parameters.AddWithValue("$activity_id", (object?)attendance.ActivityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_date_local", attendance.StartDateLocal.HasValue ? attendance.StartDateLocal.Value.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lat", (object?)attendance.StartLat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lng", (object?)attendance.StartLng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$distance_km", (object?)attendance.DistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$matched_reason", attendance.MatchedReason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ClubRunReportExistsAsync(long clubRunId, string runDate, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM club_run_reports WHERE club_run_id = $club_run_id AND run_date = $run_date LIMIT 1;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$run_date", runDate);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    private async Task<List<UserWithAuth>> ReadUsersWithAuthAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<UserWithAuth>();
        while (await reader.ReadAsync(ct))
        {
            var user = new UserRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));

            StravaAuthRecord? auth = null;
            if (!reader.IsDBNull(6))
            {
                auth = new StravaAuthRecord(
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    _tokenProtector.Unprotect(reader.GetString(8)),
                    _tokenProtector.Unprotect(reader.GetString(9)),
                    reader.GetInt64(10),
                    reader.GetString(11),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)));
            }

            list.Add(new UserWithAuth(user, auth));
        }

        return list;
    }

    private static UserRecord MapUser(SqliteDataReader reader)
    {
        return new UserRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));
    }

    private static ClubRunRecord MapClubRun(SqliteDataReader reader)
    {
        return new ClubRunRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) == 1,
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetDouble(8),
            reader.GetDouble(9),
            reader.GetDouble(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetDouble(15),
            reader.IsDBNull(16) ? null : reader.GetDouble(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(18)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(19)));
    }
}
