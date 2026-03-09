using Microsoft.Data.Sqlite;

namespace ClubRuns.App.Data;

public sealed record RunStatisticsRow(
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

public sealed partial class SqliteRepository
{
    public async Task<List<RunStatisticsRow>> GetRunStatisticsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT
    cr.id,
    c.name,
    cr.name,
    COUNT(er.event_instance_id),
    COALESCE(SUM(er.total_registered), 0),
    COALESCE(SUM(er.found_count), 0),
    COALESCE(SUM(er.not_found_count), 0),
    COALESCE(SUM(er.error_count), 0),
    COALESCE(MAX(er.found_count), 0),
    (
        SELECT ei2.event_date_local
        FROM event_reports er2
        JOIN event_instances ei2 ON ei2.id = er2.event_instance_id
        WHERE ei2.club_run_id = cr.id
        ORDER BY er2.found_count DESC, ei2.event_date_local DESC
        LIMIT 1
    ),
    MAX(ei.event_date_local)
FROM club_runs cr
JOIN clubs c ON c.id = cr.club_id
LEFT JOIN event_instances ei ON ei.club_run_id = cr.id
LEFT JOIN event_reports er ON er.event_instance_id = ei.id
GROUP BY cr.id, c.name, cr.name
ORDER BY COALESCE(SUM(er.found_count), 0) DESC, COUNT(er.event_instance_id) DESC, c.name, cr.name;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RunStatisticsRow>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RunStatisticsRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return list;
    }
}
