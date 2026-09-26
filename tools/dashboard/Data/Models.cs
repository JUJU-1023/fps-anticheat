namespace AntiCheatDashboard.Data;

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
