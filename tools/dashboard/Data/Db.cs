using System.IO;
using System.Text.Json;
using MySqlConnector;

namespace AntiCheatDashboard.Data;

/// <summary>exe 옆 dashboard.json에서 접속 정보를 읽는다. 파일이 없으면 기본값.</summary>
public sealed class DbConfig
{
    public string Server { get; set; } = "100.64.82.15";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "anticheat";
    public string User { get; set; } = "dash";
    public string Password { get; set; } = "1234";

    public static DbConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dashboard.json");
        if (!File.Exists(path)) return new DbConfig();

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
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