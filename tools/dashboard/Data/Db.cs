using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>exe 옆 dashboard.json에서 설정을 읽는다. 파일이 없으면 기본값.</summary>
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
        DefaultCommandTimeout = 10,
    }.ConnectionString;

    public string Display => $"{User}@{Server}:{Port}/{Database}";

    /// <summary>
    /// 오염 데이터 제외 조건. alias는 player_id / match_id 컬럼을 가진 테이블 별칭.
    /// 값이 설정 파일의 정수 목록뿐이라 인라인해도 주입 위험이 없다.
    /// </summary>
    public string ExclusionSql(string alias)
    {
        var sql = "";
        if (ExcludedPlayerIds.Count > 0)
            sql += $" AND {alias}.player_id NOT IN ({JoinIds(ExcludedPlayerIds)})";
        if (ExcludedMatchIds.Count > 0)
            sql += $" AND {alias}.match_id NOT IN ({JoinIds(ExcludedMatchIds)})";
        return sql;
    }

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
}
