using System.Linq;

namespace AntiCheatDashboard.Data;

// ───────── 실시간 위반 ─────────

public sealed class ViolationRow
{
    public long Id { get; init; }
    public DateTime TimeKst { get; init; }
    public long MatchId { get; init; }
    public string MatchLabel { get; init; } = "";
    public long PlayerId { get; init; }
    public string PlayerName { get; init; } = "";
    public bool IsBot { get; init; }
    public string Code { get; init; } = "";
    public string Layer { get; init; } = "";
    public int Severity { get; init; }
    public int Occurrences { get; init; }
    public int FirstTick { get; init; }
    public int LastTick { get; init; }
    public int? RttMs { get; init; }
    public string? DetailJson { get; init; }

    /// <summary>화면 강조용. 목록에 바인딩하기 전에 설정한다.</summary>
    public bool IsNew { get; set; }

    public string NewMark => IsNew ? "●" : "";
    public string TimeText => TimeKst.ToString("MM-dd HH:mm:ss.fff");
    public string MatchText => $"{MatchId} ({MatchLabel})";
    public string PlayerText => $"#{PlayerId} {PlayerName}{(IsBot ? " [BOT]" : "")}";
    public string RttText => RttMs?.ToString() ?? "-";
}

public sealed class CodeSummary
{
    public string Code { get; init; } = "";
    public long Total { get; init; }
    public int Players { get; init; }
}

public sealed class MatchOption
{
    public string Key { get; init; } = "";
    public long? Id { get; init; }
    public bool Latest { get; init; }
    public string Text { get; init; } = "";

    public static MatchOption All => new() { Key = "all", Text = "전체 매치" };
    public static MatchOption LatestMatch => new() { Key = "latest", Latest = true, Text = "최신 매치 (자동 추적)" };

    public override string ToString() => Text;
}

/// <summary>폴링마다 읽는 DB 머리 정보</summary>
public sealed class DbHead
{
    public long MaxViolationId { get; init; }
    public long LatestMatchId { get; init; }
}

// ───────── 전투 통계 공통 ─────────

/// <summary>
/// 명중·헤드샷은 (매치, server_tick) 단위로 중복 제거해서 센다.
/// 한 발이 HIT와 KILL 두 행으로 남더라도 명중 1회로 세기 위해서다.
/// 데스는 KILL 행의 target_id 기준.
/// </summary>
public class CombatStats
{
    public long Fires { get; init; }
    public long Hits { get; init; }
    public long Headshots { get; init; }
    public long Kills { get; init; }
    public long Deaths { get; init; }
    public double? AvgRtt { get; init; }

    /// <summary>코드 → SUM(occurrences). 바인딩 전에 채운다.</summary>
    public Dictionary<string, long> ViolationsByCode { get; set; } = new();

    public long ViolationTotal => ViolationsByCode.Values.Sum();
    public bool HasViolations => ViolationTotal > 0;

    public string AccuracyText => Fires > 0 ? $"{100.0 * Hits / Fires:0.0}%" : "-";
    public string HeadshotText => Hits > 0 ? $"{100.0 * Headshots / Hits:0.0}%" : "-";
    public string KdText => Deaths > 0 ? $"{(double)Kills / Deaths:0.00}" : Kills > 0 ? "∞" : "-";
    public string RttText => AvgRtt is double r ? $"{r:0}" : "-";

    public string ViolationText => ViolationsByCode.Count == 0
        ? ""
        : string.Join("  ·  ", ViolationsByCode
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{CodeFormat.Short(kv.Key)} {kv.Value:N0}"));
}

// ───────── 세션 탭 ─────────

public sealed class MatchRow
{
    public long Id { get; init; }
    public string Label { get; init; } = "";
    public DateTime StartKst { get; init; }
    public DateTime? EndKst { get; init; }
    public string? Map { get; init; }
    public string? Notes { get; init; }
    public int Players { get; init; }
    public long Fires { get; init; }
    public long Kills { get; init; }
    public long ViolationTotal { get; init; }
    public int Violators { get; init; }
    public bool Excluded { get; init; }

    public string StartText => StartKst.ToString("MM-dd HH:mm:ss");
    public string DurationText => EndKst is DateTime e ? TimeUtil.Duration(e - StartKst) : "미종료";
    public string ViolationText => ViolationTotal == 0 ? "0" : $"{ViolationTotal:N0}  ({Violators}명)";
}

public sealed class MatchPlayerRow : CombatStats
{
    public long PlayerId { get; init; }
    public string Name { get; init; } = "";
    public bool IsBot { get; init; }

    public string PlayerText => $"#{PlayerId} {Name}{(IsBot ? " [BOT]" : "")}";
}

// ───────── 플레이어 탭 ─────────

public sealed class PlayerRow : CombatStats
{
    public long PlayerId { get; init; }
    public string Name { get; init; } = "";
    public string Uid { get; init; } = "";
    public bool IsBot { get; init; }
    public DateTime FirstSeenKst { get; init; }
    public DateTime? LastSeenKst { get; init; }
    public long MatchCount { get; init; }

    /// <summary>활성 밴이 있으면 "밴 (영구)" / "밴 ~ 09-27 18:00", 없으면 빈 문자열</summary>
    public string BanStatus { get; init; } = "";
    public bool IsBanned => BanStatus.Length > 0;

    public string PlayerText => $"#{PlayerId} {Name}";
    public string BotText => IsBot ? "BOT" : "";
    public string FirstSeenText => FirstSeenKst.ToString("MM-dd HH:mm");
    public string LastSeenText => LastSeenKst?.ToString("MM-dd HH:mm") ?? "-";
}

public sealed class PlayerHistoryRow : CombatStats
{
    public long MatchId { get; init; }
    public string Label { get; init; } = "";
    public DateTime StartKst { get; init; }
    public DateTime? EndKst { get; init; }

    public string StartText => StartKst.ToString("MM-dd HH:mm");
    public string DurationText => EndKst is DateTime e ? TimeUtil.Duration(e - StartKst) : "미종료";
}

// ───────── 제재 (bans) ─────────

public sealed class BanRow
{
    // 상태 코드는 BanRepository의 SQL CASE가 DB 시계 기준으로 계산한다
    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";
    public const string KickPending = "KICK_PENDING";
    public const string KickDone = "KICK_DONE";
    public const string KickExpired = "KICK_EXPIRED";

    public long Id { get; init; }
    public long PlayerId { get; init; }
    public string Action { get; init; } = "";
    public string Reason { get; init; } = "";
    public long? ViolationId { get; init; }
    public DateTime CreatedKst { get; init; }
    public DateTime? ExpiresKst { get; init; }
    public DateTime? RevokedKst { get; init; }
    public DateTime? EnforcedKst { get; init; }
    public string CreatedBy { get; init; } = "";
    public string Status { get; init; } = "";

    public bool IsBan => Action == "BAN";
    public bool IsActive => Status == Active;
    public bool IsPending => Status == KickPending;
    /// <summary>유효한 밴, 또는 아직 집행 전인 킥은 해제(취소)할 수 있다</summary>
    public bool IsRevocable => Status is Active or KickPending;

    public string ActionText => IsBan ? "밴" : "킥";
    public string PeriodText => !IsBan ? "-" : ExpiresKst is DateTime e ? $"~ {e:MM-dd HH:mm}" : "영구";
    public string CreatedText => CreatedKst.ToString("MM-dd HH:mm:ss");
    public string ViolationText => ViolationId is long v ? $"#{v}" : "";

    public string StatusText => Status switch
    {
        Active => "유효",
        Expired => "만료",
        Revoked => RevokedKst is DateTime r ? $"해제됨 {r:MM-dd HH:mm}" : "해제됨",
        KickPending => "집행 대기",
        KickDone => "완료",
        KickExpired => "미집행 (접속 중 아님)",
        _ => Status,
    };

    public string EnforcedText => EnforcedKst is DateTime t ? $"{t:HH:mm:ss}" : IsRevocable ? "대기" : "-";
}

/// <summary>제재 창의 "근거 위반" 선택지</summary>
public sealed class ViolationOption
{
    public long? Id { get; init; }
    public string Text { get; init; } = "";
    public string SuggestedReason { get; init; } = "";

    public static ViolationOption None => new() { Text = "(연결 안 함)" };

    public static ViolationOption From(ViolationRow v) => new()
    {
        Id = v.Id,
        Text = $"#{v.Id}   {v.Code} ×{v.Occurrences:N0}   ·   {v.TimeKst:MM-dd HH:mm:ss}   ·   매치 {v.MatchId}",
        SuggestedReason = $"{v.Code} ×{v.Occurrences:N0} (매치 {v.MatchId}, 위반 #{v.Id})",
    };
}
