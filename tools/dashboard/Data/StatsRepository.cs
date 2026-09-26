using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>
/// 세션/플레이어 탭 집계. session_summary가 아직 비어 있어서 combat_events와 violations에서 직접 계산한다.
///
/// 집계 규칙
///   발사   = FIRE 행 수
///   명중   = HIT/KILL 행을 (match_id, server_tick) 단위로 중복 제거한 수
///            (한 발이 HIT와 KILL 두 행으로 남아도 1회. 한 플레이어는 한 틱에 한 발만 쏜다)
///   헤드샷 = 위 명중 중 is_headshot=1
///   킬     = KILL 행 수 (player_id = 쏜 사람)
///   데스   = KILL 행의 target_id = 이 플레이어
///   위반   = SUM(occurrences)
/// </summary>
public static class StatsRepository
{
    // ───────── 세션 탭 ─────────

    public static async Task<List<MatchRow>> GetMatchesAsync(string? label, bool exclude, int limit = 200)
    {
        var playerEx = exclude ? Db.Config.PlayerExclusion("ce.player_id") : "";
        var violEx = exclude ? Db.Config.PlayerExclusion("vv.player_id") : "";
        var matchEx = exclude ? Db.Config.MatchExclusion("m.id") : "";

        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var labelSql = "";
        if (label != null)
        {
            labelSql = " AND m.label = @label";
            cmd.Parameters.AddWithValue("@label", label);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        cmd.CommandText = $@"
SELECT m.id, m.label, m.started_at, m.ended_at, m.map_name, m.notes,
       c.players, c.fires, c.kills, v.total, v.violators
FROM matches m
LEFT JOIN (
    SELECT ce.match_id,
           COUNT(DISTINCT ce.player_id)  AS players,
           SUM(ce.event_type = 'FIRE')   AS fires,
           SUM(ce.event_type = 'KILL')   AS kills
    FROM combat_events ce
    WHERE 1=1 {playerEx}
    GROUP BY ce.match_id
) c ON c.match_id = m.id
LEFT JOIN (
    SELECT vv.match_id,
           SUM(vv.occurrences)           AS total,
           COUNT(DISTINCT vv.player_id)  AS violators
    FROM violations vv
    WHERE 1=1 {violEx}
    GROUP BY vv.match_id
) v ON v.match_id = m.id
WHERE 1=1 {labelSql} {matchEx}
ORDER BY m.id DESC
LIMIT @limit";

        var list = new List<MatchRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var id = rd.GetInt64(0);
            list.Add(new MatchRow
            {
                Id = id,
                Label = rd.GetString(1),
                StartKst = TimeUtil.ToKst(rd.GetDateTime(2)),
                EndKst = Rd.Kst(rd, 3),
                Map = Rd.S(rd, 4),
                Notes = Rd.S(rd, 5),
                Players = Rd.I(rd, 6),
                Fires = Rd.L(rd, 7),
                Kills = Rd.L(rd, 8),
                ViolationTotal = Rd.L(rd, 9),
                Violators = Rd.I(rd, 10),
                Excluded = Db.Config.ExcludedMatchIds.Contains(id),
            });
        }
        return list;
    }

    /// <summary>한 매치의 참가자별 전적. 참가자 = 이동 샘플 또는 전투 이벤트가 있는 플레이어.</summary>
    public static async Task<List<MatchPlayerRow>> GetMatchPlayersAsync(long matchId, bool exclude)
    {
        var playerEx = exclude ? Db.Config.PlayerExclusion("p.id") : "";

        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@m", matchId);
        cmd.CommandText = $@"
SELECT p.id, p.display_name, p.player_uid, p.is_bot,
       s.fires, s.hits, s.heads, s.kills, d.deaths, s.avg_rtt
FROM (
    SELECT player_id FROM movement_samples WHERE match_id = @m
    UNION
    SELECT player_id FROM combat_events WHERE match_id = @m
) pp
JOIN players p ON p.id = pp.player_id
LEFT JOIN (
    SELECT player_id,
           SUM(event_type = 'FIRE') AS fires,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL') THEN server_tick END) AS hits,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL') AND is_headshot = 1 THEN server_tick END) AS heads,
           SUM(event_type = 'KILL') AS kills,
           AVG(rtt_ms) AS avg_rtt
    FROM combat_events
    WHERE match_id = @m
    GROUP BY player_id
) s ON s.player_id = p.id
LEFT JOIN (
    SELECT target_id, COUNT(*) AS deaths
    FROM combat_events
    WHERE match_id = @m AND event_type = 'KILL' AND target_id IS NOT NULL
    GROUP BY target_id
) d ON d.target_id = p.id
WHERE 1=1 {playerEx}
ORDER BY p.is_bot, p.id";

        var rows = new List<MatchPlayerRow>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                var id = rd.GetInt64(0);
                rows.Add(new MatchPlayerRow
                {
                    PlayerId = id,
                    Name = PlayerNames.Resolve(id, Rd.S(rd, 1), Rd.S(rd, 2)),
                    IsBot = Rd.B(rd, 3),
                    Fires = Rd.L(rd, 4),
                    Hits = Rd.L(rd, 5),
                    Headshots = Rd.L(rd, 6),
                    Kills = Rd.L(rd, 7),
                    Deaths = Rd.L(rd, 8),
                    AvgRtt = Rd.D(rd, 9),
                });
            }
        }

        var pivot = await PivotAsync(conn,
            "SELECT player_id, code, SUM(occurrences) FROM violations WHERE match_id = @m GROUP BY player_id, code",
            c => c.Parameters.AddWithValue("@m", matchId));
        foreach (var r in rows)
            if (pivot.TryGetValue(r.PlayerId, out var byCode)) r.ViolationsByCode = byCode;

        return rows;
    }

    // ───────── 플레이어 탭 ─────────

    public static async Task<List<PlayerRow>> GetPlayersAsync(bool exclude, bool includeBots)
    {
        var playerEx = exclude ? Db.Config.PlayerExclusion("p.id") : "";
        var matchEx = exclude ? Db.Config.MatchExclusion("match_id") : "";
        var botSql = includeBots ? "" : " AND p.is_bot = 0";

        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT p.id, p.display_name, p.player_uid, p.is_bot, p.first_seen, p.last_seen,
       s.matches, s.fires, s.hits, s.heads, s.kills, d.deaths, s.avg_rtt,
       b.permanent, b.until_utc
FROM players p
LEFT JOIN (
    SELECT player_id,
           COUNT(DISTINCT match_id) AS matches,
           SUM(event_type = 'FIRE') AS fires,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL')
                 THEN CONCAT(match_id, ':', server_tick) END) AS hits,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL') AND is_headshot = 1
                 THEN CONCAT(match_id, ':', server_tick) END) AS heads,
           SUM(event_type = 'KILL') AS kills,
           AVG(rtt_ms) AS avg_rtt
    FROM combat_events
    WHERE 1=1 {matchEx}
    GROUP BY player_id
) s ON s.player_id = p.id
LEFT JOIN (
    SELECT target_id, COUNT(*) AS deaths
    FROM combat_events
    WHERE event_type = 'KILL' AND target_id IS NOT NULL {matchEx}
    GROUP BY target_id
) d ON d.target_id = p.id
LEFT JOIN (
    -- 지금 유효한 밴 (판정은 DB 시계 기준)
    SELECT player_id,
           MAX(expires_at IS NULL) AS permanent,
           MAX(expires_at)         AS until_utc
    FROM bans
    WHERE action = 'BAN' AND revoked_at IS NULL
      AND (expires_at IS NULL OR expires_at > NOW(3))
    GROUP BY player_id
) b ON b.player_id = p.id
WHERE 1=1 {playerEx} {botSql}
ORDER BY p.is_bot, p.id";

        var rows = new List<PlayerRow>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                var id = rd.GetInt64(0);
                var uid = Rd.S(rd, 2) ?? "";
                string ban = "";
                if (Rd.B(rd, 13)) ban = "밴 (영구)";
                else if (Rd.Kst(rd, 14) is DateTime until) ban = $"밴 ~ {until:MM-dd HH:mm}";

                rows.Add(new PlayerRow
                {
                    PlayerId = id,
                    Name = PlayerNames.Resolve(id, Rd.S(rd, 1), uid),
                    Uid = uid,
                    IsBot = Rd.B(rd, 3),
                    FirstSeenKst = TimeUtil.ToKst(rd.GetDateTime(4)),
                    LastSeenKst = Rd.Kst(rd, 5),
                    MatchCount = Rd.L(rd, 6),
                    Fires = Rd.L(rd, 7),
                    Hits = Rd.L(rd, 8),
                    Headshots = Rd.L(rd, 9),
                    Kills = Rd.L(rd, 10),
                    Deaths = Rd.L(rd, 11),
                    AvgRtt = Rd.D(rd, 12),
                    BanStatus = ban,
                });
            }
        }

        var violMatchEx = exclude ? Db.Config.MatchExclusion("match_id") : "";
        var pivot = await PivotAsync(conn,
            $"SELECT player_id, code, SUM(occurrences) FROM violations WHERE 1=1 {violMatchEx} GROUP BY player_id, code",
            null);
        foreach (var r in rows)
            if (pivot.TryGetValue(r.PlayerId, out var byCode)) r.ViolationsByCode = byCode;

        return rows;
    }

    /// <summary>한 플레이어의 매치별 이력. 참가 매치 = 쏘거나, 보이거나, 죽거나, 위반이 있었던 매치.</summary>
    public static async Task<List<PlayerHistoryRow>> GetPlayerHistoryAsync(long playerId, bool exclude)
    {
        var matchEx = exclude ? Db.Config.MatchExclusion("m.id") : "";

        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@p", playerId);
        cmd.CommandText = $@"
SELECT m.id, m.label, m.started_at, m.ended_at,
       s.fires, s.hits, s.heads, s.kills, d.deaths, s.avg_rtt
FROM (
    SELECT match_id FROM combat_events WHERE player_id = @p
    UNION
    SELECT match_id FROM combat_events WHERE target_id = @p AND event_type = 'KILL'
    UNION
    SELECT match_id FROM violations WHERE player_id = @p
) pm
JOIN matches m ON m.id = pm.match_id
LEFT JOIN (
    SELECT match_id,
           SUM(event_type = 'FIRE') AS fires,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL') THEN server_tick END) AS hits,
           COUNT(DISTINCT CASE WHEN event_type IN ('HIT','KILL') AND is_headshot = 1 THEN server_tick END) AS heads,
           SUM(event_type = 'KILL') AS kills,
           AVG(rtt_ms) AS avg_rtt
    FROM combat_events
    WHERE player_id = @p
    GROUP BY match_id
) s ON s.match_id = m.id
LEFT JOIN (
    SELECT match_id, COUNT(*) AS deaths
    FROM combat_events
    WHERE target_id = @p AND event_type = 'KILL'
    GROUP BY match_id
) d ON d.match_id = m.id
WHERE 1=1 {matchEx}
ORDER BY m.id DESC";

        var rows = new List<PlayerHistoryRow>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                rows.Add(new PlayerHistoryRow
                {
                    MatchId = rd.GetInt64(0),
                    Label = rd.GetString(1),
                    StartKst = TimeUtil.ToKst(rd.GetDateTime(2)),
                    EndKst = Rd.Kst(rd, 3),
                    Fires = Rd.L(rd, 4),
                    Hits = Rd.L(rd, 5),
                    Headshots = Rd.L(rd, 6),
                    Kills = Rd.L(rd, 7),
                    Deaths = Rd.L(rd, 8),
                    AvgRtt = Rd.D(rd, 9),
                });
            }
        }

        var pivot = await PivotAsync(conn,
            "SELECT match_id, code, SUM(occurrences) FROM violations WHERE player_id = @p GROUP BY match_id, code",
            c => c.Parameters.AddWithValue("@p", playerId));
        foreach (var r in rows)
            if (pivot.TryGetValue(r.MatchId, out var byCode)) r.ViolationsByCode = byCode;

        return rows;
    }

    // ───────── 공통 ─────────

    /// <summary>(키, code, 합계) 3열 결과를 키 → (code → 합계)로 모은다.</summary>
    private static async Task<Dictionary<long, Dictionary<string, long>>> PivotAsync(
        MySqlConnection conn, string sql, Action<MySqlCommand>? bind)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        bind?.Invoke(cmd);

        var result = new Dictionary<long, Dictionary<string, long>>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var key = rd.GetInt64(0);
            if (!result.TryGetValue(key, out var byCode))
                result[key] = byCode = new Dictionary<string, long>();
            byCode[rd.GetString(1)] = Rd.L(rd, 2);
        }
        return result;
    }
}
