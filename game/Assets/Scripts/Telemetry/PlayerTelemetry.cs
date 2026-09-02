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
        if (IsServer && string.IsNullOrEmpty(_playerUid))
            _playerUid = $"pending-{OwnerClientId}";

        if (IsOwner)
            SubmitIdentityServerRpc(new FixedString64Bytes(LocalPlayerUid()));
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && !string.IsNullOrEmpty(_playerUid))
            _claimedUids.Remove(_playerUid);
    }

    /// <summary>
    /// 클라이언트 로컬에 UUID를 한 번 만들어 보관한다.
    /// W9에서 HWID 해시로 대체할 지점.
    /// </summary>
    private static string LocalPlayerUid()
    {
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

    [ServerRpc]
    private void SubmitIdentityServerRpc(FixedString64Bytes uid)
    {
        string s = uid.ToString();
        if (string.IsNullOrEmpty(s)) return;

        // MPPM으로 같은 머신에서 여러 클라이언트를 띄우면 persistentDataPath를
        // 공유해 uid가 겹친다. 악의적으로 남의 uid를 보내는 경우도 같은 처리를 한다.
        // 실제로 서로 다른 PC라면 uid가 달라 접미사가 붙지 않는다.
        if (_claimedUids.TryGetValue(s, out ulong owner) && owner != OwnerClientId)
            s = $"{s}#{OwnerClientId}";

        _claimedUids[s] = OwnerClientId;
        _playerUid = s;
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