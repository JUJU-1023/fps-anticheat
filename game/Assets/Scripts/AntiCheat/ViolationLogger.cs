// =====================================================================
//  ViolationLogger.cs
//  경로: game/Assets/Scripts/AntiCheat/ViolationLogger.cs
//
//  위반을 1초 창 단위로 집계해 텔레메트리로 내보낸다.
//
//  왜 집계하는가
//   스피드핵 하나가 걸리면 초당 60건씩 위반이 발생한다.
//   매 건을 기록하면 violations 테이블이 폭주하고 Grafana도 못 읽는다.
//   같은 (플레이어, 코드) 조합은 1초 동안 카운트만 올리고
//   창이 끝날 때 한 행으로 내보낸다. occurrences에 횟수가 들어간다.
//
//  서버 전용. TelemetryWriter가 활성일 때만 기록한다.
// =====================================================================

using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static class ViolationLogger
{
    /// <summary>집계 창 길이(초).</summary>
    private const float WindowSec = 1.0f;

    private struct Window
    {
        public string  playerUid;
        public string  code;
        public string  layer;
        public int     severity;
        public int     firstTick;
        public int     lastTick;
        public int     count;
        public float   openedAt;      // realtimeSinceStartup
        public int     rttMs;
        public string  lastDetail;    // 마지막 위반의 사유
    }

    // (OwnerClientId, code) -> 열린 창
    private static readonly Dictionary<(ulong, string), Window> _windows = new();
    private static readonly List<(ulong, string)> _expired = new();
    private static readonly StringBuilder _sb = new StringBuilder(384);

    /// <summary>
    /// 위반 1건을 보고한다. 같은 창 안이면 카운트만 오른다.
    /// </summary>
    public static void Report(
        ulong clientId, string playerUid, string code,
        int tick, int severity, string detail, int rttMs, string layer = "L2")
    {
        float now = Time.realtimeSinceStartup;
        var key = (clientId, code);

        if (_windows.TryGetValue(key, out var w))
        {
            w.count++;
            w.lastTick   = tick;
            w.lastDetail = detail;
            w.rttMs      = rttMs;
            _windows[key] = w;
            return;
        }

        _windows[key] = new Window
        {
            playerUid  = playerUid,
            code       = code,
            layer      = layer,
            severity   = severity,
            firstTick  = tick,
            lastTick   = tick,
            count      = 1,
            openedAt   = now,
            rttMs      = rttMs,
            lastDetail = detail,
        };
    }

    /// <summary>
    /// 만료된 창을 내보낸다. 서버 FixedUpdate에서 매 틱 호출한다.
    /// </summary>
    public static void Tick()
    {
        if (_windows.Count == 0) return;

        var w0 = TelemetryWriter.Instance;
        if (w0 == null || !w0.IsActive) return;

        float now = Time.realtimeSinceStartup;

        _expired.Clear();
        foreach (var kv in _windows)
            if (now - kv.Value.openedAt >= WindowSec)
                _expired.Add(kv.Key);

        foreach (var key in _expired)
        {
            var w = _windows[key];
            _windows.Remove(key);
            Emit(w0, w);
        }
    }

    /// <summary>세션 종료 시 남은 창을 모두 내보낸다.</summary>
    public static void FlushAll()
    {
        var w0 = TelemetryWriter.Instance;
        if (w0 == null || !w0.IsActive) { _windows.Clear(); return; }

        foreach (var kv in _windows) Emit(w0, kv.Value);
        _windows.Clear();
    }

    private static void Emit(TelemetryWriter writer, Window w)
    {
        _sb.Clear();
        _sb.Append("{\"t\":\"violation\"");
        _sb.Append(",\"match_uid\":").Append(TJson.Str(writer.MatchUid));
        _sb.Append(",\"player_uid\":").Append(TJson.Str(w.playerUid));
        _sb.Append(",\"code\":").Append(TJson.Str(w.code));
        _sb.Append(",\"layer\":").Append(TJson.Str(w.layer));
        _sb.Append(",\"severity\":").Append(w.severity.ToString(TJson.Inv));
        _sb.Append(",\"first_tick\":").Append(w.firstTick.ToString(TJson.Inv));
        _sb.Append(",\"last_tick\":").Append(w.lastTick.ToString(TJson.Inv));
        _sb.Append(",\"occurrences\":").Append(w.count.ToString(TJson.Inv));
        _sb.Append(",\"ts\":").Append(TJson.Str(TJson.Now()));
        _sb.Append(",\"rtt_ms\":").Append(w.rttMs >= 0 ? w.rttMs.ToString(TJson.Inv) : "null");
        _sb.Append(",\"detail\":{\"reason\":").Append(TJson.Str(w.lastDetail ?? "")).Append('}');
        _sb.Append('}');

        writer.Write(_sb.ToString());

        Debug.Log($"[VIOLATION] {w.code} uid={w.playerUid} " +
                  $"ticks={w.firstTick}~{w.lastTick} count={w.count} reason={w.lastDetail}");
    }

    /// <summary>서버 재시작 시 상태 정리.</summary>
    public static void Clear() => _windows.Clear();
}
