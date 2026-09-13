// =====================================================================
//  PlayerTelemetry.cs
//  경로: game/Assets/Scripts/Telemetry/PlayerTelemetry.cs
//
//  플레이어별 이동 텔레메트리 샘플러. 서버에서만 동작한다.
//  PlayerCharacter 프리팹에 붙인다.
//
//  샘플링 설계
//   - 저장은 10Hz(서버 FixedUpdate 6번마다 1행), 관측은 60Hz.
//   - 창을 "입력 6개마다"가 아니라 "서버 FixedUpdate 6번마다"로 자른다.
//     입력 개수로 자르면 input_count가 항상 6이라 스피드핵 신호가 사라진다.
//     서버의 실제 시간으로 잘라야 "100ms에 입력이 18개 왔다"가 드러난다.
//   - 창 안의 60Hz 원본에서 yaw_rate_max / speed_max를 뽑아 같은 행에 넣는다.
//     에임 스냅은 50~80ms 안에 끝나므로 10Hz 원시값만으로는 소실된다.
//
//  RTT
//   - 반드시 서버 측정값을 쓴다. 클라이언트 보고값을 쓰면
//     치터가 RTT를 부풀려 검증 관용 범위를 넓힐 수 있다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 4 : player_uid 안정화 ★
//
//  실측된 문제 두 가지 (players 테이블 id 13~22)
//
//   (1) 접미사가 OwnerClientId 였다
//
//       MPPM 은 persistentDataPath 를 공유하므로 두 번째 클라이언트의
//       uid 가 항상 충돌한다. 충돌 시 붙이던 접미사가 OwnerClientId 인데,
//       이 값은 접속 순서대로 증가하고 재사용되지 않는다.
//
//         09-08 세션  ...dda7#2
//         09-13 세션  ...8ea4#5
//
//       같은 사람이 세션마다 다른 플레이어가 된다. W13 Trust Score 는
//       플레이어 단위 누적이므로 이 상태로는 feature 가 성립하지 않는다.
//
//       → 빈 번호 중 가장 작은 값을 쓴다. 접속 순서가 같으면 두 번째
//         인스턴스는 항상 #1 을 받는다.
//
//   (2) pending-N 이 DB 에 저장됐다
//
//       OnNetworkSpawn 에서 임시 uid 를 넣고 식별 RPC 는 왕복 뒤에
//       도착한다. 그 사이에 나간 텔레메트리가 그대로 players 에 행을
//       만들었다 (id 13~18).
//
//       → 식별이 확정될 때까지 텔레메트리를 내보내지 않는다.
//         손실은 1~2틱이고, V-MOVE 스폰 유예가 3초이므로 그 구간에는
//         어차피 판정이 없다.
//
//  식별의 한계
//   uid 는 클라이언트가 선언한다. 남의 uid 를 보내는 것을 막을 수 없다.
//   W9 의 HWID 해시로 대체하더라도 클라이언트 보고값인 것은 같다.
//   근본적으로는 계정 시스템이 필요하며, 이 프로젝트의 범위를 벗어난다.
//   발표에서는 "L2 는 세션 내 행위를 보고, 세션 간 연결은 계정 계층의
//   책임"으로 정리한다.
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerTelemetry : NetworkBehaviour
{
    /// <summary>60Hz -> 10Hz. 서버 FixedUpdate 기준.</summary>
    private const int SampleEveryTicks = 6;

    /// <summary>식별이 이 시간 안에 안 오면 경고한다(초).</summary>
    private const float IdentityTimeoutSec = 5f;

    /// <summary>충돌 시 붙일 수 있는 접미사 상한. 이 이상은 비정상이다.</summary>
    private const int MaxUidSuffix = 16;

    [Tooltip("봇이면 체크. is_bot이 섞이면 Isolation Forest가 봇을 이상치로 학습한다.")]
    [SerializeField] private bool isBot = false;

    // -----------------------------------------------------------------
    //  식별
    // -----------------------------------------------------------------

    /// <summary>
    /// 서버가 이미 배정한 uid 목록. 중복을 막는다.
    /// static이므로 서버 프로세스 전체에서 공유된다.
    /// </summary>
    private static readonly Dictionary<string, ulong> _claimedUids = new Dictionary<string, ulong>();

    private string _playerUid = "";
    public string PlayerUid => _playerUid;

    /// <summary>
    /// 클라이언트가 보낸 uid 로 확정됐는가.
    ///
    /// false 인 동안에는 텔레메트리를 내보내지 않는다. 임시 uid 가
    /// DB 에 행을 만들면 같은 사람이 여러 플레이어로 쪼개진다.
    /// </summary>
    private bool _identityResolved;

    /// <summary>서버에서 스폰된 시각. 식별 타임아웃 경고에 쓴다.</summary>
    private float _spawnedAt;
    private bool _identityWarned;

    // -----------------------------------------------------------------
    //  창 누적 (서버 전용)
    // -----------------------------------------------------------------

    private int _tickInWindow;
    private int _inputCount;
    private float _yawRateMax;
    private float _pitchRateMax;
    private float _speedMax;

    private bool _hasPrevAngles;
    private float _prevYaw, _prevPitch;

    // 창의 마지막 상태
    private bool _hasAnySample;
    private int _lastServerTick, _lastClientTick;
    private Vector3 _lastPos, _lastVel;
    private float _lastYaw, _lastPitch;
    private byte _lastButtons;
    private bool _lastGrounded;

    private readonly StringBuilder _sb = new StringBuilder(384);

    // -----------------------------------------------------------------
    //  수명 주기
    // -----------------------------------------------------------------

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            _spawnedAt = Time.realtimeSinceStartup;
            _identityResolved = false;

            // 임시값. 식별이 확정될 때까지 텔레메트리는 나가지 않으므로
            // 이 문자열이 DB 에 들어가는 일은 없다. 로그 식별용이다.
            if (string.IsNullOrEmpty(_playerUid))
                _playerUid = $"pending-{OwnerClientId}";
        }

        if (IsOwner)
            SubmitIdentityServerRpc(new FixedString64Bytes(LocalPlayerUid()));
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && _identityResolved && !string.IsNullOrEmpty(_playerUid))
            _claimedUids.Remove(_playerUid);
    }

    /// <summary>
    /// 클라이언트 식별자를 정한다. 우선순위는 다음과 같다.
    ///
    ///   1) -playerUid 명령줄 인자
    ///      한 PC 에서 빌드를 여러 개 띄울 때 명시적으로 지정한다.
    ///      측정 세션에서 누가 누구인지 확정하는 가장 확실한 방법이다.
    ///   2) persistentDataPath 의 player_uid.txt
    ///      서로 다른 PC 라면 이것만으로 충분하다.
    ///   3) 세션 한정 UUID (파일을 못 쓰는 경우 폴백)
    ///
    /// W9 에서 HWID 해시로 대체할 지점이다. 다만 어느 쪽이든
    /// 클라이언트가 선언하는 값이라는 한계는 남는다.
    /// </summary>
    private static string LocalPlayerUid()
    {
        string fromArgs = ReadUidFromCommandLine();
        if (!string.IsNullOrEmpty(fromArgs)) return fromArgs;

        string path = Path.Combine(Application.persistentDataPath, "player_uid.txt");
        try
        {
            if (File.Exists(path))
            {
                string s = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(s) && s.Length <= 60) return s;
            }
            string uid = Guid.NewGuid().ToString();
            File.WriteAllText(path, uid);
            return uid;
        }
        catch
        {
            // 파일을 못 쓰면 세션 한정 UUID로 폴백
            return Guid.NewGuid().ToString();
        }
    }

    /// <summary>`-playerUid &lt;값&gt;` 을 읽는다. 없으면 빈 문자열.</summary>
    private static string ReadUidFromCommandLine()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "-playerUid", StringComparison.OrdinalIgnoreCase))
                    continue;

                string v = args[i + 1].Trim();
                if (!string.IsNullOrEmpty(v) && v.Length <= 60) return v;
            }
        }
        catch { /* 플랫폼에 따라 접근이 막힐 수 있다 */ }

        return "";
    }

    [ServerRpc]
    private void SubmitIdentityServerRpc(FixedString64Bytes uid)
    {
        string baseUid = uid.ToString();
        if (string.IsNullOrEmpty(baseUid)) return;

        // 같은 클라이언트가 두 번 보내면 무시한다.
        if (_identityResolved) return;

        string resolved = ResolveUid(baseUid, OwnerClientId);
        if (resolved == null)
        {
            Debug.LogError(
                $"[TELEMETRY] uid 충돌이 {MaxUidSuffix}개를 넘었다. client={OwnerClientId} base={baseUid}");
            return;
        }

        _claimedUids[resolved] = OwnerClientId;
        _playerUid = resolved;
        _identityResolved = true;

        if (resolved != baseUid)
            Debug.Log($"[TELEMETRY] uid 충돌 → {resolved} (client={OwnerClientId})");
    }

    /// <summary>
    /// 충돌하지 않는 uid 를 고른다.
    ///
    /// ★ 접미사에 OwnerClientId 를 쓰면 안 된다 ★
    /// 그 값은 접속 순서대로 증가하고 재사용되지 않으므로, 같은
    /// 인스턴스가 세션마다 다른 번호를 받는다. 실측에서 같은 사람이
    /// #2 와 #5 로 쪼개졌다 (players id 21, 22).
    ///
    /// 빈 번호 중 가장 작은 값을 쓰면 접속 순서가 같은 한 결과가
    /// 재현된다. MPPM 두 번째 인스턴스는 항상 #1 이 된다.
    /// </summary>
    /// <returns>배정할 uid. 자리가 없으면 null.</returns>
    private static string ResolveUid(string baseUid, ulong clientId)
    {
        // 이미 자기가 들고 있던 자리면 그대로 쓴다(재연결).
        if (_claimedUids.TryGetValue(baseUid, out ulong owner))
        {
            if (owner == clientId) return baseUid;
        }
        else return baseUid;

        for (int n = 1; n <= MaxUidSuffix; n++)
        {
            string candidate = $"{baseUid}#{n}";
            if (!_claimedUids.TryGetValue(candidate, out ulong o) || o == clientId)
                return candidate;
        }

        return null;
    }

    // -----------------------------------------------------------------
    //  입력 훅 — PlayerController.SubmitInputServerRpc 에서 호출
    // -----------------------------------------------------------------

    /// <summary>
    /// 서버가 입력 하나를 시뮬레이션한 직후 호출한다.
    /// </summary>
    public void OnServerInput(
        int serverTick, int clientTick,
        Vector3 pos, Vector3 vel,
        float yaw, float pitch,
        byte buttons, bool grounded)
    {
        if (!IsServer) return;

        _inputCount++;

        // 각속도: 서버 틱 간격 기준. Time.fixedDeltaTime이 곧 틱 간격이다.
        if (_hasPrevAngles)
        {
            float dt = Mathf.Max(Time.fixedDeltaTime, 1e-5f);
            float yawRate = Mathf.Abs(Mathf.DeltaAngle(_prevYaw, yaw)) / dt;
            float pitchRate = Mathf.Abs(Mathf.DeltaAngle(_prevPitch, pitch)) / dt;

            if (yawRate > _yawRateMax) _yawRateMax = yawRate;
            if (pitchRate > _pitchRateMax) _pitchRateMax = pitchRate;
        }
        _prevYaw = yaw;
        _prevPitch = pitch;
        _hasPrevAngles = true;

        // 수평 속도만 본다. 낙하 속도는 중력이라 의미가 다르다.
        float speed = new Vector2(vel.x, vel.z).magnitude;
        if (speed > _speedMax) _speedMax = speed;

        _lastServerTick = serverTick;
        _lastClientTick = clientTick;
        _lastPos = pos;
        _lastVel = vel;
        _lastYaw = yaw;
        _lastPitch = pitch;
        _lastButtons = buttons;
        _lastGrounded = grounded;
        _hasAnySample = true;
    }

    // -----------------------------------------------------------------
    //  샘플 방출
    // -----------------------------------------------------------------

    private void FixedUpdate()
    {
        if (!IsServer) return;

        // --- 식별 대기 ---
        // 확정 전에 내보내면 pending-N 이 players 테이블에 행을 만든다.
        // 실측에서 6개가 그렇게 생겼다 (id 13~18).
        if (!_identityResolved)
        {
            WarnIfIdentityLate();
            _tickInWindow = 0;
            ResetWindow();
            return;
        }

        var w = TelemetryWriter.Instance;
        if (w == null || !w.IsActive) return;

        _tickInWindow++;
        if (_tickInWindow < SampleEveryTicks) return;
        _tickInWindow = 0;

        // 입력이 한 번도 안 온 상태에서는 기록할 게 없다.
        if (!_hasAnySample) return;

        // 창 안에 입력이 0개여도 기록한다. 패킷 손실/끊김의 증거가 된다.
        EmitSample(w);
        ResetWindow();
    }

    /// <summary>
    /// 식별이 늦으면 한 번만 경고한다.
    ///
    /// 정상 클라이언트는 OnNetworkSpawn 에서 곧바로 RPC 를 보내므로
    /// RTT 왕복(수 ms) 안에 도착한다. 몇 초가 지나도 안 오면
    /// 식별 RPC 를 의도적으로 빼놓은 클라이언트일 수 있다.
    /// 그 경우 이 플레이어의 텔레메트리는 아예 남지 않는다.
    /// </summary>
    private void WarnIfIdentityLate()
    {
        if (_identityWarned) return;
        if (Time.realtimeSinceStartup - _spawnedAt < IdentityTimeoutSec) return;

        _identityWarned = true;
        Debug.LogWarning(
            $"[TELEMETRY] 식별 미수신 {IdentityTimeoutSec}초 경과. " +
            $"client={OwnerClientId} 텔레메트리를 기록하지 않는다.");
    }

    private void ResetWindow()
    {
        _inputCount = 0;
        _yawRateMax = 0f;
        _pitchRateMax = 0f;
        _speedMax = 0f;
        _hasPrevAngles = false;   // 창 경계를 넘는 각속도는 계산하지 않는다
    }

    private void EmitSample(TelemetryWriter w)
    {
        int rtt = GetServerRttMs();

        _sb.Clear();
        _sb.Append("{\"t\":\"move\"");
        _sb.Append(",\"match_uid\":").Append(TJson.Str(w.MatchUid));
        _sb.Append(",\"player_uid\":").Append(TJson.Str(_playerUid));
        _sb.Append(",\"is_bot\":").Append(isBot ? "true" : "false");
        _sb.Append(",\"server_tick\":").Append(_lastServerTick.ToString(TJson.Inv));
        _sb.Append(",\"client_tick\":").Append(_lastClientTick.ToString(TJson.Inv));
        _sb.Append(",\"ts\":").Append(TJson.Str(TJson.Now()));

        _sb.Append(",\"pos\":[")
           .Append(TJson.F(_lastPos.x)).Append(',')
           .Append(TJson.F(_lastPos.y)).Append(',')
           .Append(TJson.F(_lastPos.z)).Append(']');

        _sb.Append(",\"vel\":[")
           .Append(TJson.F(_lastVel.x)).Append(',')
           .Append(TJson.F(_lastVel.y)).Append(',')
           .Append(TJson.F(_lastVel.z)).Append(']');

        _sb.Append(",\"yaw\":").Append(TJson.F(_lastYaw));
        _sb.Append(",\"pitch\":").Append(TJson.F(_lastPitch));
        _sb.Append(",\"buttons\":").Append(((int)_lastButtons).ToString(TJson.Inv));
        _sb.Append(",\"grounded\":").Append(_lastGrounded ? "true" : "false");

        _sb.Append(",\"yaw_rate_max\":").Append(TJson.F(_yawRateMax));
        _sb.Append(",\"pitch_rate_max\":").Append(TJson.F(_pitchRateMax));
        _sb.Append(",\"speed_max\":").Append(TJson.F(_speedMax));
        _sb.Append(",\"input_count\":").Append(_inputCount.ToString(TJson.Inv));

        _sb.Append(",\"rtt_ms\":")
           .Append(rtt >= 0 ? rtt.ToString(TJson.Inv) : "null");

        _sb.Append('}');

        w.Write(_sb.ToString());
    }

    /// <summary>
    /// 서버가 측정한 RTT. 클라이언트 보고값을 절대 쓰지 않는다.
    /// </summary>
    private int GetServerRttMs()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig == null || nm.NetworkConfig.NetworkTransport == null)
            return -1;

        // 호스트 자기 자신은 RTT 0
        if (nm.IsHost && OwnerClientId == nm.LocalClientId) return 0;

        try
        {
            return (int)nm.NetworkConfig.NetworkTransport.GetCurrentRtt(OwnerClientId);
        }
        catch
        {
            return -1;
        }
    }
}