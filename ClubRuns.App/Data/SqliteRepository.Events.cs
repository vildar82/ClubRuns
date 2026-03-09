using Microsoft.Data.Sqlite;

namespace ClubRuns.App.Data;

public sealed partial class SqliteRepository
{
    public async Task InitializeEventSchemaAsync(string defaultTimeZoneId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
CREATE TABLE IF NOT EXISTS clubs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    slug TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    time_zone_id TEXT NOT NULL,
    is_active INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS event_instances (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    club_run_id INTEGER NOT NULL,
    event_date_local TEXT NOT NULL,
    starts_at_utc TEXT NOT NULL,
    window_start_utc TEXT NOT NULL,
    window_end_utc TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE (club_run_id, event_date_local),
    FOREIGN KEY(club_run_id) REFERENCES club_runs(id)
);

CREATE TABLE IF NOT EXISTS event_registrations (
    event_instance_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    status TEXT NOT NULL,
    registered_at_utc TEXT NOT NULL,
    source TEXT NOT NULL,
    PRIMARY KEY (event_instance_id, user_id),
    FOREIGN KEY(event_instance_id) REFERENCES event_instances(id),
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS event_results (
    event_instance_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    status TEXT NOT NULL,
    strava_activity_id INTEGER,
    matched_at_utc TEXT,
    activity_start_utc TEXT,
    activity_start_local TEXT,
    start_lat REAL,
    start_lng REAL,
    distance_km REAL,
    matched_reason TEXT,
    error_message TEXT,
    PRIMARY KEY (event_instance_id, user_id),
    FOREIGN KEY(event_instance_id) REFERENCES event_instances(id),
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS event_reports (
    event_instance_id INTEGER PRIMARY KEY,
    generated_at_utc TEXT NOT NULL,
    total_registered INTEGER NOT NULL,
    found_count INTEGER NOT NULL,
    not_found_count INTEGER NOT NULL,
    error_count INTEGER NOT NULL,
    report_chat_id INTEGER,
    FOREIGN KEY(event_instance_id) REFERENCES event_instances(id)
);

CREATE TABLE IF NOT EXISTS strava_request_queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_instance_id INTEGER,
    user_id INTEGER,
    request_type TEXT NOT NULL,
    status TEXT NOT NULL,
    requested_at_utc TEXT NOT NULL,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    last_error TEXT,
    FOREIGN KEY(event_instance_id) REFERENCES event_instances(id),
    FOREIGN KEY(user_id) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS ix_event_instances_club_run_id_event_date_local ON event_instances(club_run_id, event_date_local);
CREATE INDEX IF NOT EXISTS ix_event_registrations_user_id ON event_registrations(user_id);
CREATE INDEX IF NOT EXISTS ix_event_results_status ON event_results(status);
CREATE INDEX IF NOT EXISTS ix_strava_request_queue_status_requested_at_utc ON strava_request_queue(status, requested_at_utc);
";

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }


    }

    public async Task<ClubRecord?> GetClubByIdAsync(long clubId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, slug, name, time_zone_id, is_active, created_at, updated_at
FROM clubs
WHERE id = $id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", clubId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapClub(reader);
    }

    public async Task<ClubRecord> UpsertClubAsync(long? id, string name, string slug, string timeZoneId, bool isActive, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO clubs (id, slug, name, time_zone_id, is_active, created_at, updated_at)
VALUES ($id, $slug, $name, $time_zone_id, $is_active, COALESCE($created_at, $updated_at), $updated_at)
ON CONFLICT(id) DO UPDATE SET
    slug = excluded.slug,
    name = excluded.name,
    time_zone_id = excluded.time_zone_id,
    is_active = excluded.is_active,
    updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$time_zone_id", timeZoneId);
        cmd.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at", id.HasValue ? DBNull.Value : now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        await cmd.ExecuteNonQueryAsync(ct);

        if (id.HasValue)
        {
            return (await GetClubByIdAsync(id.Value, ct))!;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = @"
SELECT id, slug, name, time_zone_id, is_active, created_at, updated_at
FROM clubs
WHERE slug = $slug
LIMIT 1;";
        select.Parameters.AddWithValue("$slug", slug);
        await using var reader = await select.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapClub(reader);
    }
    public async Task<List<ClubRecord>> GetClubsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, slug, name, time_zone_id, is_active, created_at, updated_at
FROM clubs
ORDER BY name;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ClubRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapClub(reader));
        }

        return list;
    }




    public async Task<bool> ClubHasRunsAsync(long clubId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM club_runs WHERE club_id = $club_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$club_id", clubId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> DeleteClubAsync(long clubId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM clubs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", clubId);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }
    public async Task<EventInstanceRecord> EnsureEventInstanceAsync(
        long clubRunId,
        string eventDateLocal,
        DateTimeOffset startsAtUtc,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO event_instances (
    club_run_id, event_date_local, starts_at_utc, window_start_utc, window_end_utc, status, created_at_utc, updated_at_utc)
VALUES (
    $club_run_id, $event_date_local, $starts_at_utc, $window_start_utc, $window_end_utc, 'scheduled', $now, $now)
ON CONFLICT(club_run_id, event_date_local) DO UPDATE SET
    starts_at_utc = excluded.starts_at_utc,
    window_start_utc = excluded.window_start_utc,
    window_end_utc = excluded.window_end_utc,
    updated_at_utc = excluded.updated_at_utc;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$event_date_local", eventDateLocal);
        cmd.Parameters.AddWithValue("$starts_at_utc", startsAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$window_start_utc", windowStartUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$window_end_utc", windowEndUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        return (await GetEventInstanceAsync(clubRunId, eventDateLocal, ct))!;
    }

    public async Task<EventInstanceRecord?> GetEventInstanceAsync(long clubRunId, string eventDateLocal, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, club_run_id, event_date_local, starts_at_utc, window_start_utc, window_end_utc, status, created_at_utc, updated_at_utc
FROM event_instances
WHERE club_run_id = $club_run_id AND event_date_local = $event_date_local
LIMIT 1;";
        cmd.Parameters.AddWithValue("$club_run_id", clubRunId);
        cmd.Parameters.AddWithValue("$event_date_local", eventDateLocal);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MapEventInstance(reader);
    }

    public async Task UpsertEventRegistrationAsync(long eventInstanceId, long userId, string status, string source, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO event_registrations (event_instance_id, user_id, status, registered_at_utc, source)
VALUES ($event_instance_id, $user_id, $status, $registered_at_utc, $source)
ON CONFLICT(event_instance_id, user_id) DO UPDATE SET
    status = excluded.status,
    registered_at_utc = excluded.registered_at_utc,
    source = excluded.source;";
        cmd.Parameters.AddWithValue("$event_instance_id", eventInstanceId);
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$registered_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$source", source);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsEventRegistrationActiveAsync(long eventInstanceId, long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT 1
FROM event_registrations
WHERE event_instance_id = $event_instance_id AND user_id = $user_id AND status = 'registered'
LIMIT 1;";
        cmd.Parameters.AddWithValue("$event_instance_id", eventInstanceId);
        cmd.Parameters.AddWithValue("$user_id", userId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<List<UserWithAuth>> GetEventRegistrationsWithAuthAsync(long eventInstanceId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
    u.id, u.telegram_user_id, u.telegram_username, u.first_name, u.last_name, u.created_at,
    s.user_id, s.athlete_id, s.access_token, s.refresh_token, s.expires_at, s.scope, s.last_auth_at
FROM event_registrations r
JOIN users u ON u.id = r.user_id
LEFT JOIN strava_auth s ON s.user_id = u.id
WHERE r.event_instance_id = $event_instance_id AND r.status = 'registered'
ORDER BY u.telegram_username, u.first_name, u.last_name, u.id;";
        cmd.Parameters.AddWithValue("$event_instance_id", eventInstanceId);

        return await ReadUsersWithAuthAsync(cmd, ct);
    }

    public async Task<List<(ClubRunRecord Run, EventInstanceRecord Event)>> GetUserUpcomingRegistrationsAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT cr.id, cr.club_id, cr.name, cr.slug, cr.is_active, cr.day_of_week, cr.hour, cr.minute_from, cr.minute_to,
       cr.start_lat, cr.start_lng, cr.radius_km, cr.window_start_local, cr.window_end_local, cr.target_start_local,
       cr.allowed_activity_types, cr.min_distance_km, cr.max_distance_km, cr.report_chat_id, cr.created_at, cr.updated_at,
       ei.id, ei.club_run_id, ei.event_date_local, ei.starts_at_utc, ei.window_start_utc, ei.window_end_utc, ei.status, ei.created_at_utc, ei.updated_at_utc
FROM event_registrations er
JOIN event_instances ei ON ei.id = er.event_instance_id
JOIN club_runs cr ON cr.id = ei.club_run_id
WHERE er.user_id = $user_id AND er.status = 'registered' AND ei.event_date_local >= $today
ORDER BY ei.event_date_local, cr.hour, cr.minute_from, cr.name;";
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$today", DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(ClubRunRecord, EventInstanceRecord)>();
        while (await reader.ReadAsync(ct))
        {
            var run = MapClubRun(reader);
            var ev = new EventInstanceRecord(
                reader.GetInt64(21),
                reader.GetInt64(22),
                reader.GetString(23),
                DateTimeOffset.Parse(reader.GetString(24)),
                DateTimeOffset.Parse(reader.GetString(25)),
                DateTimeOffset.Parse(reader.GetString(26)),
                reader.GetString(27),
                DateTimeOffset.Parse(reader.GetString(28)),
                DateTimeOffset.Parse(reader.GetString(29)));
            list.Add((run, ev));
        }

        return list;
    }

    public async Task UpsertEventResultAsync(EventResultUpsert result, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO event_results (
    event_instance_id, user_id, status, strava_activity_id, matched_at_utc, activity_start_utc, activity_start_local,
    start_lat, start_lng, distance_km, matched_reason, error_message)
VALUES (
    $event_instance_id, $user_id, $status, $strava_activity_id, $matched_at_utc, $activity_start_utc, $activity_start_local,
    $start_lat, $start_lng, $distance_km, $matched_reason, $error_message)
ON CONFLICT(event_instance_id, user_id) DO UPDATE SET
    status = excluded.status,
    strava_activity_id = excluded.strava_activity_id,
    matched_at_utc = excluded.matched_at_utc,
    activity_start_utc = excluded.activity_start_utc,
    activity_start_local = excluded.activity_start_local,
    start_lat = excluded.start_lat,
    start_lng = excluded.start_lng,
    distance_km = excluded.distance_km,
    matched_reason = excluded.matched_reason,
    error_message = excluded.error_message;";
        cmd.Parameters.AddWithValue("$event_instance_id", result.EventInstanceId);
        cmd.Parameters.AddWithValue("$user_id", result.UserId);
        cmd.Parameters.AddWithValue("$status", result.Status);
        cmd.Parameters.AddWithValue("$strava_activity_id", (object?)result.StravaActivityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$matched_at_utc", (object?)result.MatchedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity_start_utc", (object?)result.ActivityStartUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity_start_local", (object?)result.ActivityStartLocal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lat", (object?)result.StartLat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start_lng", (object?)result.StartLng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$distance_km", (object?)result.DistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$matched_reason", (object?)result.MatchedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_message", (object?)result.ErrorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> EventReportExistsAsync(long eventInstanceId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM event_reports WHERE event_instance_id = $event_instance_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$event_instance_id", eventInstanceId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task SaveEventReportAsync(long eventInstanceId, int totalRegistered, int foundCount, int notFoundCount, int errorCount, long? reportChatId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO event_reports (event_instance_id, generated_at_utc, total_registered, found_count, not_found_count, error_count, report_chat_id)
VALUES ($event_instance_id, $generated_at_utc, $total_registered, $found_count, $not_found_count, $error_count, $report_chat_id)
ON CONFLICT(event_instance_id) DO UPDATE SET
    generated_at_utc = excluded.generated_at_utc,
    total_registered = excluded.total_registered,
    found_count = excluded.found_count,
    not_found_count = excluded.not_found_count,
    error_count = excluded.error_count,
    report_chat_id = excluded.report_chat_id;";
        cmd.Parameters.AddWithValue("$event_instance_id", eventInstanceId);
        cmd.Parameters.AddWithValue("$generated_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$total_registered", totalRegistered);
        cmd.Parameters.AddWithValue("$found_count", foundCount);
        cmd.Parameters.AddWithValue("$not_found_count", notFoundCount);
        cmd.Parameters.AddWithValue("$error_count", errorCount);
        cmd.Parameters.AddWithValue("$report_chat_id", (object?)reportChatId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> EnqueueStravaRequestAsync(long? eventInstanceId, long? userId, string requestType, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO strava_request_queue (event_instance_id, user_id, request_type, status, requested_at_utc)
VALUES ($event_instance_id, $user_id, $request_type, 'pending', $requested_at_utc);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$event_instance_id", (object?)eventInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$user_id", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$request_type", requestType);
        cmd.Parameters.AddWithValue("$requested_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        var id = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(id);
    }

    public async Task<int> CountRecentCompletedStravaRequestsAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(*)
FROM strava_request_queue
WHERE status IN ('processing', 'done') AND COALESCE(started_at_utc, requested_at_utc) >= $since_utc;";
        cmd.Parameters.AddWithValue("$since_utc", sinceUtc.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<DateTimeOffset?> GetOldestRecentStravaRequestAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT MIN(COALESCE(started_at_utc, requested_at_utc))
FROM strava_request_queue
WHERE status IN ('processing', 'done') AND COALESCE(started_at_utc, requested_at_utc) >= $since_utc;";
        cmd.Parameters.AddWithValue("$since_utc", sinceUtc.ToString("O"));
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string s ? DateTimeOffset.Parse(s) : null;
    }

    public async Task MarkStravaRequestProcessingAsync(long requestId, CancellationToken ct = default)
    {
        await UpdateStravaRequestStatusAsync(requestId, "processing", null, DateTimeOffset.UtcNow.ToString("O"), null, ct);
    }

    public async Task MarkStravaRequestDoneAsync(long requestId, CancellationToken ct = default)
    {
        await UpdateStravaRequestStatusAsync(requestId, "done", null, null, DateTimeOffset.UtcNow.ToString("O"), ct);
    }

    public async Task MarkStravaRequestFailedAsync(long requestId, string error, CancellationToken ct = default)
    {
        await UpdateStravaRequestStatusAsync(requestId, "failed", error, null, DateTimeOffset.UtcNow.ToString("O"), ct);
    }

    private async Task UpdateStravaRequestStatusAsync(long requestId, string status, string? lastError, string? startedAtUtc, string? completedAtUtc, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE strava_request_queue
SET status = $status,
    last_error = COALESCE($last_error, last_error),
    started_at_utc = COALESCE($started_at_utc, started_at_utc),
    completed_at_utc = COALESCE($completed_at_utc, completed_at_utc)
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$last_error", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started_at_utc", (object?)startedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed_at_utc", (object?)completedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", requestId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ClubRecord MapClub(SqliteDataReader reader)
    {
        return new ClubRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) == 1,
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    private static EventInstanceRecord MapEventInstance(SqliteDataReader reader)
    {
        return new EventInstanceRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
    }
}





