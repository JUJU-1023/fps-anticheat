using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>exe 옆 dashboard.json에서 설정을 읽는다. 파일이 없으면 기본값. // 주석 허용.</summary>
public sealed class DbConfig
{
    public string Server { get; set; } = "100.64.82.15";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "anticheat";
    public string User { get; set; } = "dash";
    public string Password { get; set; } = "1234";

    /// <summary>실시간 탭 폴링 주기(초)</summary>
    public int PollSeconds { get; set; } = 3;

    /// <summary>위반 목록 최대 행 수</summary>
    public int MaxRows { get; set; } = 300;

    /// <summary>uid 버그 시기 중복 플레이어 행 (13~18 pending-N, 21 #2, 22 #5)</summary>
    public List<long> ExcludedPlayerIds { get; set; } = new() { 13, 14, 15, 16, 17, 18, 21, 22 };

    /// <summary>클라 탄약 예측 버그 세션 (label=unknown)</summary>
    public List<long> ExcludedMatchIds { get; set; } = new() { 97, 98 };

    /// <summary>players.id(문자열 키) → 표시 이름. 대시보드 표시에만 쓰고 DB는 건드리지 않는다.</summary>
    public Dictionary<string, string> PlayerAliases { get; set; } = new();

    public static DbConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dashboard.json");
        if (!File.Exists(path)) return new DbConfig();

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<DbConfig>(File.ReadAllText(path), opts) ?? new DbConfig();
    }

    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Server,
        Port = (uint)Port,
        Database = Database,
        UserID = User,
        Password = Password,
        ConnectionTimeout = 5,
        DefaultCommandTimeout = 15,
    }.ConnectionString;

    public string Display => $"{User}@{Server}:{Port}/{Database}";

    // 제외 조건. 값이 설정 파일의 정수 목록뿐이라 인라인해도 주입 위험이 없다.

    /// <summary>예: PlayerExclusion("v.player_id") → " AND v.player_id NOT IN (13,...)"</summary>
    public string PlayerExclusion(string column) =>
        ExcludedPlayerIds.Count == 0 ? "" : $" AND {column} NOT IN ({JoinIds(ExcludedPlayerIds)})";

    /// <summary>예: MatchExclusion("m.id") → " AND m.id NOT IN (97,98)"</summary>
    public string MatchExclusion(string column) =>
        ExcludedMatchIds.Count == 0 ? "" : $" AND {column} NOT IN ({JoinIds(ExcludedMatchIds)})";

    /// <summary>player_id / match_id 컬럼을 가진 테이블 별칭에 두 조건을 모두 적용</summary>
    public string ExclusionSql(string alias) =>
        PlayerExclusion($"{alias}.player_id") + MatchExclusion($"{alias}.match_id");

    private static string JoinIds(IEnumerable<long> ids) =>
        string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
}

public static class Db
{
    public static DbConfig Config { get; } = DbConfig.Load();

    public static async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(Config.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

/// <summary>
/// DB는 UTC로 돈다. 화면 표시만 이 PC 시각대(KST)로 바꾸고,
/// 시각 비교·기록(밴 만료 등)은 항상 SQL 안에서 NOW(3) 기준으로 한다.
/// </summary>
public static class TimeUtil
{
    public static DateTime ToKst(DateTime dbUtc) =>
        DateTime.SpecifyKind(dbUtc, DateTimeKind.Utc).ToLocalTime();

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return "-";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes:00}분";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}분 {t.Seconds:00}초";
        return $"{t.Seconds}초";
    }
}

/// <summary>별칭 → display_name → uid 앞부분 순서로 표시 이름을 정한다.</summary>
public static class PlayerNames
{
    public static string Resolve(long id, string? displayName, string? uid)
    {
        if (Db.Config.PlayerAliases.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var alias)
            && !string.IsNullOrWhiteSpace(alias))
            return alias;
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
        if (uid is null) return "?";
        return uid.Length <= 12 ? uid : uid[..12] + "…";
    }
}

/// <summary>"V-MOVE-01" → "MOVE". 형식이 다르면 원문.</summary>
public static class CodeFormat
{
    public static string Short(string code)
    {
        var parts = code.Split('-');
        return parts.Length >= 3 && parts[0] == "V" ? string.Join("-", parts[1..^1]) : code;
    }
}

/// <summary>NULL 안전 읽기</summary>
internal static class Rd
{
    public static long L(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
    public static int I(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    public static double? D(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
    public static string? S(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static bool B(MySqlDataReader r, int i) => !r.IsDBNull(i) && Convert.ToBoolean(r.GetValue(i));
    public static DateTime? Kst(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : TimeUtil.ToKst(r.GetDateTime(i));
}
