using System.Linq;
using MySqlConnector;

namespace AntiCheatDashboard.Data;

public sealed class ViolationFilter
{
    public string? Code { get; set; }
    public string? Layer { get; set; }
    public long? MatchId { get; set; }
    public bool LatestMatch { get; set; }
    public bool ApplyExclusions { get; set; } = true;
    public int Limit { get; set; } = 300;

    /// <summary>요약 카드 집계 구간(분). null이면 전체 기간.</summary>
    public int? SummaryWindowMinutes { get; set; }
}

/// <summary>
/// violations 조회. 행 하나가 집계 단위라 횟수는 항상 SUM(occurrences).
/// </summary>
public static class ViolationRepository
{
    public static async Task<List<ViolationRow>> GetRecentAsync(ViolationFilter f)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var where = "WHERE 1=1";
        if (f.Code != null)
        {
            where += " AND v.code = @code";
            cmd.Parameters.AddWithValue("@code", f.Code);
        }
        if (f.Layer != null)
        {
            where += " AND v.layer = @layer";
            cmd.Parameters.AddWithValue("@layer", f.Layer);
        }
        where += MatchClause(f, cmd);
        if (f.ApplyExclusions) where += Db.Config.ExclusionSql("v");

        cmd.Parameters.AddWithValue("@limit", f.Limit);
        cmd.CommandText = $@"
SELECT v.id, v.server_time, v.match_id, m.label,
       v.player_id, p.display_name, p.player_uid, p.is_bot,
       v.code, v.layer, v.severity, v.occurrences,
       v.first_tick, v.last_tick, v.rtt_ms, v.detail_json
FROM violations v
LEFT JOIN players p ON p.id = v.player_id
LEFT JOIN matches m ON m.id = v.match_id
{where}
ORDER BY v.server_time DESC, v.id DESC
LIMIT @limit";

        var list = new List<ViolationRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var playerId = rd.GetInt64(4);

            list.Add(new ViolationRow
            {
                Id = rd.GetInt64(0),
                TimeKst = TimeUtil.ToKst(rd.GetDateTime(1)),
                MatchId = rd.GetInt64(2),
                MatchLabel = rd.IsDBNull(3) ? "?" : rd.GetString(3),
                PlayerId = playerId,
                PlayerName = PlayerNames.Resolve(playerId, Rd.S(rd, 5), Rd.S(rd, 6)),
                IsBot = !rd.IsDBNull(7) && Convert.ToBoolean(rd.GetValue(7)),
                Code = rd.GetString(8),
                Layer = rd.GetString(9),
                Severity = Convert.ToInt32(rd.GetValue(10)),
                Occurrences = Convert.ToInt32(rd.GetValue(11)),
                FirstTick = Convert.ToInt32(rd.GetValue(12)),
                LastTick = Convert.ToInt32(rd.GetValue(13)),
                RttMs = rd.IsDBNull(14) ? null : Convert.ToInt32(rd.GetValue(14)),
                DetailJson = rd.IsDBNull(15) ? null : rd.GetString(15),
            });
        }
        return list;
    }

    /// <summary>코드별 합계. 코드/레이어 필터는 무시하고 매치·구간·제외만 적용.</summary>
    public static async Task<List<CodeSummary>> GetSummaryAsync(ViolationFilter f)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var where = "WHERE 1=1";
        if (f.SummaryWindowMinutes is int min)
        {
            // 구간 계산은 DB 시계(UTC) 기준
            where += " AND v.server_time >= NOW(3) - INTERVAL @min MINUTE";
            cmd.Parameters.AddWithValue("@min", min);
        }
        where += MatchClause(f, cmd);
        if (f.ApplyExclusions) where += Db.Config.ExclusionSql("v");

        cmd.CommandText = $@"
SELECT v.code, SUM(v.occurrences), COUNT(DISTINCT v.player_id)
FROM violations v
{where}
GROUP BY v.code
ORDER BY v.code";

        var list = new List<CodeSummary>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new CodeSummary
            {
                Code = rd.GetString(0),
                Total = Convert.ToInt64(rd.GetValue(1)),
                Players = Convert.ToInt32(rd.GetValue(2)),
            });
        }
        return list;
    }

    public static async Task<List<string>> GetCodesAsync()
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT DISTINCT code FROM violations ORDER BY code", conn);
        var list = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) list.Add(rd.GetString(0));
        return list;
    }

    public static async Task<List<MatchOption>> GetRecentMatchesAsync(int limit = 40)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT id, label, started_at, ended_at FROM matches ORDER BY id DESC LIMIT @n", conn);
        cmd.Parameters.AddWithValue("@n", limit);

        var list = new List<MatchOption>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var id = rd.GetInt64(0);
            var label = rd.GetString(1);
            var start = TimeUtil.ToKst(rd.GetDateTime(2));
            var open = rd.IsDBNull(3);
            var excluded = Db.Config.ExcludedMatchIds.Contains(id) ? " · 제외 대상" : "";
            list.Add(new MatchOption
            {
                Key = $"m:{id}",
                Id = id,
                Text = $"#{id}  {label}  {start:MM-dd HH:mm}{(open ? " · 미종료" : "")}{excluded}",
            });
        }
        return list;
    }

    public static async Task<DbHead> GetHeadAsync()
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT (SELECT COALESCE(MAX(id),0) FROM violations), (SELECT COALESCE(MAX(id),0) FROM matches)", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return new DbHead
        {
            MaxViolationId = Convert.ToInt64(rd.GetValue(0)),
            LatestMatchId = Convert.ToInt64(rd.GetValue(1)),
        };
    }

    private static string MatchClause(ViolationFilter f, MySqlCommand cmd)
    {
        if (f.LatestMatch) return " AND v.match_id = (SELECT MAX(id) FROM matches)";
        if (f.MatchId is long id)
        {
            cmd.Parameters.AddWithValue("@matchId", id);
            return " AND v.match_id = @matchId";
        }
        return "";
    }
}
