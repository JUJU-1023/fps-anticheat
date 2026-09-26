using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>
/// bans 읽기/쓰기. dash 계정은 bans INSERT와 revoked_at UPDATE만 가능하다.
///
/// 시각 규칙: 만료 시각, 상태 판정은 전부 SQL 안에서 DB 시계(UTC) NOW(3)로 계산한다.
/// 이 PC 시각으로 expires_at을 만들면 9시간 어긋난 밴이 들어간다.
///
/// 킥 규칙: 킥은 발행 후 KickWindowSeconds 안에만 유효하다. 그 안에 서버가 집행하지 못하면
/// (대상이 접속 중이 아니었으면) 효력 없이 만료된다. VM101의 ban_sync도 같은 값을 써야 한다.
/// </summary>
public static class BanRepository
{
    public const int KickWindowSeconds = 60;

    private static readonly string StatusSql = $@"
        CASE
            WHEN revoked_at IS NOT NULL THEN 'REVOKED'
            WHEN action = 'KICK' AND enforced_at IS NOT NULL THEN 'KICK_DONE'
            WHEN action = 'KICK' AND created_at > NOW(3) - INTERVAL {KickWindowSeconds} SECOND THEN 'KICK_PENDING'
            WHEN action = 'KICK' THEN 'KICK_EXPIRED'
            WHEN expires_at IS NOT NULL AND expires_at <= NOW(3) THEN 'EXPIRED'
            ELSE 'ACTIVE'
        END";

    public static async Task<List<BanRow>> GetForPlayerAsync(long playerId)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@p", playerId);
        cmd.CommandText = $@"
SELECT id, player_id, action, reason, violation_id,
       created_at, expires_at, revoked_at, enforced_at, created_by,
       {StatusSql} AS status
FROM bans
WHERE player_id = @p
ORDER BY id DESC";

        var list = new List<BanRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new BanRow
            {
                Id = rd.GetInt64(0),
                PlayerId = rd.GetInt64(1),
                Action = rd.GetString(2),
                Reason = rd.GetString(3),
                ViolationId = rd.IsDBNull(4) ? null : rd.GetInt64(4),
                CreatedKst = TimeUtil.ToKst(rd.GetDateTime(5)),
                ExpiresKst = Rd.Kst(rd, 6),
                RevokedKst = Rd.Kst(rd, 7),
                EnforcedKst = Rd.Kst(rd, 8),
                CreatedBy = rd.GetString(9),
                Status = rd.GetString(10),
            });
        }
        return list;
    }

    /// <summary>
    /// 제재 발행. action은 "KICK" 또는 "BAN". BAN에서 durationMinutes가 null이면 영구.
    /// 반환값은 새 bans.id.
    /// </summary>
    public static async Task<long> IssueAsync(long playerId, string action, string reason,
                                              long? violationId, int? durationMinutes)
    {
        if (action is not ("KICK" or "BAN")) throw new ArgumentException("action은 KICK 또는 BAN", nameof(action));

        await using var conn = await Db.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var expiresSql = "NULL";
        if (action == "BAN" && durationMinutes is int min)
        {
            expiresSql = "NOW(3) + INTERVAL @min MINUTE";
            cmd.Parameters.AddWithValue("@min", min);
        }

        cmd.Parameters.AddWithValue("@p", playerId);
        cmd.Parameters.AddWithValue("@a", action);
        cmd.Parameters.AddWithValue("@r", reason.Length > 255 ? reason[..255] : reason);
        cmd.Parameters.AddWithValue("@v", (object?)violationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@by", IssuerName());

        cmd.CommandText = $@"
INSERT INTO bans (player_id, action, reason, violation_id, expires_at, created_by)
VALUES (@p, @a, @r, @v, {expiresSql}, @by)";

        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    /// <summary>해제. 이미 해제된 행이면 false. DELETE가 아니라 revoked_at 기록이라 이력이 남는다.</summary>
    public static async Task<bool> RevokeAsync(long banId)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = new MySqlCommand(
            "UPDATE bans SET revoked_at = NOW(3) WHERE id = @id AND revoked_at IS NULL", conn);
        cmd.Parameters.AddWithValue("@id", banId);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>created_by(varchar 32)에 남길 발행자. 예: "dash:im384"</summary>
    private static string IssuerName()
    {
        var name = $"dash:{Environment.UserName}";
        return name.Length > 32 ? name[..32] : name;
    }
}
