// =====================================================================
//  BanEnforcer.cs
//  경로: game/Assets/Scripts/AntiCheat/BanEnforcer.cs
//
//  대시보드에서 낸 킥/밴을 게임 서버가 집행한다. 서버에서만 동작한다.
//
//  흐름
//    대시보드 ─INSERT→ bans (VM102)
//    ban_sync.py (VM101) ─2초마다→ active_bans.json (원자적 교체)
//    BanEnforcer ─0.5초마다→ 파일 mtime 확인, 접속자 uid 대조 → DisconnectClient
//                 └ 집행 이벤트를 텔레메트리 JSONL 로 → ingester 가 bans.enforced_at 기록
//
//  ★ 빌드 프리즈를 깨지 않기 위한 제약 ★
//   - NetworkBehaviour 가 아니다. RPC / NetworkVariable 을 추가하면
//     프리즈된 클라이언트와 서버의 네트워크 정의가 달라진다.
//   - 씬에 배치하지 않는다. RuntimeInitializeOnLoadMethod 로 런타임에 만든다.
//     씬을 건드리면 MapManifest 기하 해시와 씬 오브젝트 구성이 바뀐다.
//   - PlayerTelemetry 를 수정하지 않는다. 공개된 PlayerUid 만 읽는다.
//   → 서버만 재빌드하고 클라이언트는 프리즈 빌드를 그대로 쓴다.
//
//  집행 시점
//   - 접속 중인 플레이어가 새로 밴/킥 됨        → kind "live"
//   - 밴된 플레이어가 접속해 식별이 확정됨      → kind "reconnect_rejected"
//   식별 RPC 가 도착해 uid 가 확정되기 전(pending-N)에는 판정하지 않는다.
//   따라서 밴된 플레이어도 접속 직후 최대 0.5초 남짓은 게임 안에 있다.
//
//  파일이 오래됐을 때 (ban_sync 중단)
//   마지막으로 읽은 목록으로 계속 집행한다(기존 밴은 유지, 새 밴과 만료는
//   반영 안 됨). 30초 넘게 갱신이 없으면 한 번 경고한다.
//
//  킥
//   ban_sync 는 발행 60초 이내, 미집행 킥만 파일에 넣는다. 집행한 킥 id 는
//   여기서 기억해서, enforced_at 이 DB 에 반영되기 전에 재접속해도 두 번
//   끊지 않는다.
//
//  한계
//   uid 는 클라이언트가 선언한다(PlayerTelemetry 주석 참고). uid 파일을
//   지우거나 -playerUid 로 다른 값을 넣으면 밴을 우회한다. 계정 계층의 몫.
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public sealed class BanEnforcer : MonoBehaviour
{
    private const float CheckIntervalSec = 0.5f;
    private const float StaleWarnSec = 30f;
    private const string DefaultBanFile = "/home/game/gameserver/bans/active_bans.json";

    // --- active_bans.json 구조 (ban_sync.py 와 일치) ---

    [Serializable]
    private class BanFile
    {
        public string db_now;
        public BanEntry[] bans;
        public KickEntry[] kicks;
    }

    [Serializable]
    private class BanEntry
    {
        public long id;
        public string player_uid;
        public string expires_at;   // null/빈 문자열 = 영구 (로그용, 판정은 ban_sync 가 끝냄)
    }

    [Serializable]
    private class KickEntry
    {
        public long id;
        public string player_uid;
    }

    private struct Target
    {
        public ulong ClientId;
        public string Uid;
        public long BanId;
        public string Action;   // "BAN" / "KICK"
        public string Kind;     // "live" / "reconnect_rejected"
    }

    // -----------------------------------------------------------------

    private static BanEnforcer _instance;

    private string _path;
    private DateTime _loadedMtime = DateTime.MinValue;
    private string _banSignature = "";

    private readonly Dictionary<string, BanEntry> _bans = new Dictionary<string, BanEntry>(StringComparer.Ordinal);
    private readonly Dictionary<string, KickEntry> _kicks = new Dictionary<string, KickEntry>(StringComparer.Ordinal);
    private readonly HashSet<long> _handledKicks = new HashSet<long>();

    /// <summary>uid 확정 상태로 한 번이라도 본 클라이언트. 처음 보는데 밴이면 재접속 거부.</summary>
    private readonly HashSet<ulong> _identifiedClients = new HashSet<ulong>();

    /// <summary>끊기를 요청한 클라이언트. 실제로 빠질 때까지 중복 요청하지 않는다.</summary>
    private readonly HashSet<ulong> _disconnecting = new HashSet<ulong>();

    private readonly List<Target> _targets = new List<Target>();
    private readonly List<ulong> _prune = new List<ulong>();
    private readonly StringBuilder _sb = new StringBuilder(256);

    private float _nextCheck;
    private bool _announced, _warnedMissing, _warnedStale;

    // -----------------------------------------------------------------
    //  생성 — 씬을 건드리지 않고 런타임에 만든다
    // -----------------------------------------------------------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject("[BanEnforcer]");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BanEnforcer>();
    }

    private void Awake()
    {
        string arg = ReadArg("-banFile");
        _path = string.IsNullOrEmpty(arg) ? DefaultBanFile : arg;
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + CheckIntervalSec;

        // 클라이언트와 서버 시작 전에는 아무것도 하지 않는다
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || !nm.IsListening) return;

        if (!_announced)
        {
            _announced = true;
            Debug.Log($"[BAN] 집행기 동작 file={_path} interval={CheckIntervalSec}s");
        }

        Reload();
        Enforce(nm);
    }

    // -----------------------------------------------------------------
    //  파일 읽기
    // -----------------------------------------------------------------

    private void Reload()
    {
        DateTime mtime;
        try
        {
            if (!File.Exists(_path))
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    Debug.LogWarning($"[BAN] 밴 파일 없음: {_path} — ban_sync 가 도는지 확인. 생길 때까지 집행 없음");
                }
                return;
            }
            _warnedMissing = false;
            mtime = File.GetLastWriteTimeUtc(_path);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BAN] 밴 파일 확인 실패: {e.Message}");
            return;
        }

        double age = (DateTime.UtcNow - mtime).TotalSeconds;
        if (age > StaleWarnSec)
        {
            if (!_warnedStale)
            {
                _warnedStale = true;
                Debug.LogWarning($"[BAN] 밴 파일이 {age:0}초째 갱신 안 됨. 마지막 목록으로 계속 집행");
            }
        }
        else if (_warnedStale)
        {
            _warnedStale = false;
            Debug.Log("[BAN] 밴 파일 갱신 재개");
        }

        if (mtime == _loadedMtime) return;

        BanFile file;
        try
        {
            // ban_sync 가 tmp → rename 으로 교체하므로 반쯤 쓰인 파일을 읽을 일은 없다
            file = JsonUtility.FromJson<BanFile>(File.ReadAllText(_path));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BAN] 밴 파일 파싱 실패, 이전 목록 유지: {e.Message}");
            return;
        }
        if (file == null) return;
        _loadedMtime = mtime;

        _bans.Clear();
        _kicks.Clear();

        var sig = new StringBuilder();
        if (file.bans != null)
        {
            foreach (var b in file.bans)
            {
                if (b == null || string.IsNullOrEmpty(b.player_uid)) continue;
                _bans[b.player_uid] = b;
                sig.Append(b.id).Append(',');
            }
        }
        if (file.kicks != null)
        {
            foreach (var k in file.kicks)
            {
                if (k == null || string.IsNullOrEmpty(k.player_uid)) continue;
                if (_handledKicks.Contains(k.id)) continue;   // 이미 집행, DB 반영 대기 중
                _kicks[k.player_uid] = k;
            }
        }

        // 목록이 바뀔 때만 로그 (2초마다 찍으면 server.log 가 묻힌다)
        string signature = sig.ToString();
        if (signature != _banSignature)
        {
            _banSignature = signature;
            Debug.Log($"[BAN] 유효 밴 {_bans.Count}건 (db_now={file.db_now})");
        }
    }

    // -----------------------------------------------------------------
    //  집행
    // -----------------------------------------------------------------

    private void Enforce(NetworkManager nm)
    {
        // 빠져나간 클라이언트 정리
        _prune.Clear();
        foreach (var id in _identifiedClients)
            if (!nm.ConnectedClients.ContainsKey(id)) _prune.Add(id);
        foreach (var id in _disconnecting)
            if (!nm.ConnectedClients.ContainsKey(id)) _prune.Add(id);
        foreach (var id in _prune)
        {
            _identifiedClients.Remove(id);
            _disconnecting.Remove(id);
        }

        if (_bans.Count == 0 && _kicks.Count == 0)
        {
            // 목록이 비어 있어도 "처음 본 클라이언트" 기록은 계속해야
            // 나중에 밴이 생겼을 때 live / reconnect 를 구분할 수 있다
            TrackIdentified(nm);
            return;
        }

        // ★ 순회 중에 DisconnectClient 를 부르면 ConnectedClientsList 가 바뀐다.
        //    대상을 먼저 모으고 루프 밖에서 끊는다.
        _targets.Clear();
        foreach (var client in nm.ConnectedClientsList)
        {
            ulong cid = client.ClientId;
            if (cid == NetworkManager.ServerClientId) continue;
            if (_disconnecting.Contains(cid)) continue;

            string uid = ResolvedUid(client);
            if (uid == null) continue;

            bool firstSight = _identifiedClients.Add(cid);

            if (_bans.TryGetValue(uid, out var ban))
            {
                _targets.Add(new Target
                {
                    ClientId = cid, Uid = uid, BanId = ban.id, Action = "BAN",
                    Kind = firstSight ? "reconnect_rejected" : "live",
                });
            }
            else if (_kicks.TryGetValue(uid, out var kick))
            {
                _handledKicks.Add(kick.id);
                _kicks.Remove(uid);
                _targets.Add(new Target
                {
                    ClientId = cid, Uid = uid, BanId = kick.id, Action = "KICK", Kind = "live",
                });
            }
        }

        foreach (var t in _targets)
            Disconnect(nm, t);
    }

    private void TrackIdentified(NetworkManager nm)
    {
        foreach (var client in nm.ConnectedClientsList)
        {
            if (client.ClientId == NetworkManager.ServerClientId) continue;
            if (ResolvedUid(client) != null) _identifiedClients.Add(client.ClientId);
        }
    }

    /// <summary>식별이 확정된 uid. 아직 pending 이거나 플레이어 오브젝트가 없으면 null.</summary>
    private static string ResolvedUid(NetworkClient client)
    {
        var po = client.PlayerObject;
        if (po == null) return null;

        var tel = po.GetComponent<PlayerTelemetry>();
        if (tel == null) return null;

        string uid = tel.PlayerUid;
        if (string.IsNullOrEmpty(uid) || uid.StartsWith("pending-", StringComparison.Ordinal)) return null;
        return uid;
    }

    private void Disconnect(NetworkManager nm, Target t)
    {
        _disconnecting.Add(t.ClientId);
        Debug.Log($"[BAN] {t.Action} #{t.BanId} 집행 ({t.Kind}) client={t.ClientId} uid={t.Uid}");

        // 끊기 전에 기록한다. 끊는 과정에서 예외가 나도 집행 사실은 남는다.
        EmitEnforced(t);

        try
        {
            string reason = t.Action == "BAN" ? $"BANNED #{t.BanId}" : $"KICKED #{t.BanId}";
            nm.DisconnectClient(t.ClientId, reason);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BAN] DisconnectClient 실패 client={t.ClientId}: {e.Message}");
        }
    }

    /// <summary>ingester 가 이 줄을 보고 bans.enforced_at 을 채운다 (처음 한 번만).</summary>
    private void EmitEnforced(Target t)
    {
        var w = TelemetryWriter.Instance;
        if (w == null || !w.IsActive)
        {
            Debug.LogWarning($"[BAN] 텔레메트리 비활성 — #{t.BanId} 의 enforced_at 이 DB 에 기록되지 않는다");
            return;
        }

        _sb.Clear();
        _sb.Append("{\"t\":\"ban_enforced\"");
        _sb.Append(",\"match_uid\":").Append(TJson.Str(w.MatchUid));
        _sb.Append(",\"player_uid\":").Append(TJson.Str(t.Uid));
        _sb.Append(",\"ban_id\":").Append(t.BanId.ToString(TJson.Inv));
        _sb.Append(",\"action\":").Append(TJson.Str(t.Action));
        _sb.Append(",\"kind\":").Append(TJson.Str(t.Kind));
        _sb.Append(",\"client_id\":").Append(t.ClientId.ToString(TJson.Inv));
        _sb.Append(",\"ts\":").Append(TJson.Str(TJson.Now()));
        _sb.Append('}');

        w.Write(_sb.ToString());
    }

    // -----------------------------------------------------------------

    private static string ReadArg(string name)
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].Trim();
        }
        catch { /* 무시 */ }
        return null;
    }
}
