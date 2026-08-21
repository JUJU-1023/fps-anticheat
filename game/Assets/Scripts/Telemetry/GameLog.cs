using System;
using System.Globalization;
using UnityEngine;

public static class GameLog
{
    public static void Emit(string level, string evt, object payload = null)
    {
        var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var json = payload != null ? JsonUtility.ToJson(payload) : "{}";
        Debug.Log($"{{\"ts\":\"{ts}\",\"level\":\"{level}\",\"event\":\"{evt}\",\"data\":{json}}}");
    }

    public static void Info(string evt, object p = null) => Emit("info", evt, p);
    public static void Warn(string evt, object p = null) => Emit("warn", evt, p);
    public static void Error(string evt, object p = null) => Emit("error", evt, p);
}