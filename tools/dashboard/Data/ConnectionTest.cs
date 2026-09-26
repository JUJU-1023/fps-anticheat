using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>
/// Day 1 진단: 접속, 서버 시각대, 테이블 행 수, 그리고 dash 계정 권한이
/// 의도대로(읽기 전체 + bans INSERT + bans.revoked_at UPDATE만) 잡혔는지 확인한다.
/// 쓰기 검사는 전부 WHERE 1=0이라 실제로 바뀌는 행은 없다.
/// 권한 검사는 실행 전에 일어나므로 행이 0개여도 거부 여부가 드러난다.
/// </summary>
public static class ConnectionTest
{
    private const int TableAccessDenied = 1142;
    private const int ColumnAccessDenied = 1143;

    public static async Task<(bool ok, List<string> lines)> RunAsync()
    {
        var lines = new List<string>();
        var ok = true;

        await using var conn = await Db.OpenAsync();
        lines.Add($"[OK]   접속 성공: {Db.Config.Display}");

        // 1) 서버 정보와 시각대
        await using (var cmd = new MySqlCommand(
            "SELECT VERSION(), CURRENT_USER(), NOW(3), UTC_TIMESTAMP(3)", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            await rd.ReadAsync();
            var now = rd.GetDateTime(2);
            var utc = rd.GetDateTime(3);
            var offsetH = Math.Round((now - utc).TotalHours, 1);
            lines.Add($"[INFO] 버전 {rd.GetString(0)} / 계정 {rd.GetString(1)}");
            lines.Add($"[INFO] DB NOW() {now:yyyy-MM-dd HH:mm:ss} (UTC{offsetH:+0.#;-0.#;+0}) / 이 PC {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        // 2) 행 수
        foreach (var table in new[] { "matches", "players", "violations", "combat_events", "bans" })
        {
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{table}`", conn);
            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            lines.Add($"[INFO] {table,-14} {n,10:N0} 행");
        }

        // 3) 권한: 거부돼야 하는 것
        ok &= await ExpectDenied(conn, lines, "violations DELETE", "DELETE FROM violations WHERE 1=0");
        ok &= await ExpectDenied(conn, lines, "players UPDATE",    "UPDATE players SET display_name = display_name WHERE 1=0");
        ok &= await ExpectDenied(conn, lines, "bans DELETE",       "DELETE FROM bans WHERE 1=0");
        ok &= await ExpectDenied(conn, lines, "bans.reason UPDATE", "UPDATE bans SET reason = reason WHERE 1=0");

        // 4) 권한: 허용돼야 하는 것
        ok &= await ExpectAllowed(conn, lines, "bans.revoked_at UPDATE", "UPDATE bans SET revoked_at = revoked_at WHERE 1=0");

        lines.Add(ok ? "[PASS] 권한 구성이 의도와 일치" : "[FAIL] 권한 구성 확인 필요");
        return (ok, lines);
    }

    private static async Task<bool> ExpectDenied(MySqlConnection conn, List<string> lines, string label, string sql)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            lines.Add($"[FAIL] {label}: 허용됨 (거부돼야 함)");
            return false;
        }
        catch (MySqlException ex) when (ex.Number is TableAccessDenied or ColumnAccessDenied)
        {
            lines.Add($"[OK]   {label}: 거부됨 ({ex.Number})");
            return true;
        }
    }

    private static async Task<bool> ExpectAllowed(MySqlConnection conn, List<string> lines, string label, string sql)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            lines.Add($"[OK]   {label}: 허용됨");
            return true;
        }
        catch (MySqlException ex)
        {
            lines.Add($"[FAIL] {label}: 거부됨 ({ex.Number} {ex.Message})");
            return false;
        }
    }
}
