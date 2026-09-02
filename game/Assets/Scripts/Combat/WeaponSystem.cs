// =====================================================================
//  WeaponSystem.cs
//  경로: game/Assets/Scripts/Combat/WeaponSystem.cs
//
//  사격. PlayerCharacter 프리팹에 붙인다.
//
//  구조
//   클라이언트: 반동을 시야에 적용한다(체감용).
//   서버      : 발사 판정, 되감기, 히트스캔, 데미지, 텔레메트리.
//
//  발사 신호는 InputPayload.buttons 의 BTN_FIRE 비트로 온다.
//  별도 RPC 를 만들지 않는 이유는 두 가지다.
//   1) V-MOVE-01 의 토큰 버킷이 사격에도 그대로 적용된다.
//      입력이 거부되면 발사도 사라진다.
//   2) 이동과 사격이 같은 틱 타임라인 위에 놓여 되감기 계산이 단순해진다.
//
//  예측하지 않는다
//   사격은 서버가 판정하고 결과만 통보한다. 이동 시뮬레이션을 건드리지
//   않으므로 W5 에서 확보한 재조정률 0% 가 보호된다.
// =====================================================================

using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class WeaponSystem : NetworkBehaviour
{
    private const byte BTN_FIRE = 1 << 1;

    // --- 서버 상태 ---
    private int _lastFireTick = -1000;
    private int _shotIndex    = 0;      // 연사 중 몇 번째 발인지 (반동 인덱스)

    // --- 클라 상태 (반동 체감용) ---
    private int _clientLastFireTick = -1000;
    private int _clientShotIndex    = 0;

    private PlayerHealth _health;
    private PlayerRewind _rewind;
    private PlayerTelemetry _telemetry;

    private static readonly StringBuilder _sb = new StringBuilder(384);

    /// <summary>레이캐스트 대상: 히트박스 + 기본(벽).</summary>
    private int _raycastMask;

    public override void OnNetworkSpawn()
    {
        _health    = GetComponent<PlayerHealth>();
        _rewind    = GetComponent<PlayerRewind>();
        _telemetry = GetComponent<PlayerTelemetry>();

        int hitbox = LayerMask.NameToLayer(WeaponConfig.HitboxLayerName);
        _raycastMask = (1 << LayerMask.NameToLayer("Default"));
        if (hitbox >= 0) _raycastMask |= (1 << hitbox);
    }

    // -----------------------------------------------------------------
    //  클라이언트: 반동
    // -----------------------------------------------------------------

    /// <summary>
    /// 소유 클라이언트가 발사 입력을 만들 때 호출한다.
    /// 반동만큼 시야를 밀어 올린다. 서버도 같은 패턴을 알고 있으므로
    /// 이 값이 조작되면 서버 계산과 어긋난다.
    /// </summary>
    /// <returns>이번 틱에 실제로 발사됐으면 반동(x=yaw, y=pitch), 아니면 zero</returns>
    public Vector2 ClientTryFire(int tick, bool firePressed)
    {
        if (!firePressed) return Vector2.zero;
        if (_health != null && _health.IsDead) return Vector2.zero;

        // 연사를 쉬었으면 반동 인덱스 초기화
        if (tick - _clientLastFireTick > WeaponConfig.RecoilResetTicks)
            _clientShotIndex = 0;

        if (tick - _clientLastFireTick < WeaponConfig.FireIntervalTicks)
            return Vector2.zero;

        _clientLastFireTick = tick;
        Vector2 recoil = WeaponConfig.GetRecoil(_clientShotIndex);
        _clientShotIndex++;
        return recoil;
    }

    // -----------------------------------------------------------------
    //  서버: 발사 처리
    // -----------------------------------------------------------------

    /// <summary>
    /// 서버가 입력을 시뮬레이션한 직후 호출한다.
    /// 발사 조건을 만족하면 되감기 후 히트스캔을 수행한다.
    /// </summary>
    public void ServerProcessInput(InputPayload input, int serverTick, int rttMs)
    {
        if (!IsServer) return;
        if ((input.buttons & BTN_FIRE) == 0) return;
        if (_health != null && _health.IsDead) return;

        // 연사 중단 판정
        if (input.tick - _lastFireTick > WeaponConfig.RecoilResetTicks)
            _shotIndex = 0;

        // ★ V-FIRE-01 이 들어갈 자리 (W7) ★
        //   지금은 서버가 간격을 강제하기만 한다.
        //   W7 에서 "간격 미만 요청이 얼마나 자주 오는가"를 위반으로 기록한다.
        if (input.tick - _lastFireTick < WeaponConfig.FireIntervalTicks) return;

        _lastFireTick = input.tick;
        int shotIndex = _shotIndex;
        _shotIndex++;

        FireHitscan(input, serverTick, rttMs, shotIndex);
    }

    private void FireHitscan(InputPayload input, int serverTick, int rttMs, int shotIndex)
    {
        // --- 조준 원점과 방향 ---
        // 클라이언트가 보낸 위치는 쓰지 않는다. 서버의 권위 위치에서
        // 눈 오프셋을 더해 재구성한다.
        Vector3 origin = transform.position + WeaponConfig.EyeOffset;
        Vector3 dir    = Quaternion.Euler(input.pitch, input.yaw, 0f) * Vector3.forward;

        // --- 되감기 시간 ---
        // RTT/2 + 보간 지연. 둘 다 서버가 아는 값이라 조작 여지가 없다.
        float rewindSec = Mathf.Clamp(
            (rttMs > 0 ? rttMs / 2000f : 0f) + WeaponConfig.InterpolationDelaySec,
            0f, WeaponConfig.MaxRewindSec);

        float targetTime = Time.realtimeSinceStartup - rewindSec;

        // --- 되감기 ---
        var rewound = new List<PlayerRewind>();
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var pr = kv.Value.GetComponent<PlayerRewind>();
            if (pr == null || pr == _rewind) continue;
            if (pr.RewindTo(targetTime)) rewound.Add(pr);
        }

        // 사수 자신의 히트박스는 원점이 그 안에 있으므로 제외한다.
        _rewind?.SetHitboxesActive(false);

        bool  hit        = false;
        bool  headshot   = false;
        float dist       = 0f;
        PlayerHealth victim = null;

        if (Physics.Raycast(origin, dir, out RaycastHit rh,
                            WeaponConfig.MaxRange, _raycastMask,
                            QueryTriggerInteraction.Ignore))
        {
            dist = rh.distance;
            var vr = rh.collider.GetComponentInParent<PlayerRewind>();
            if (vr != null)
            {
                hit      = true;
                headshot = (rh.collider == vr.HeadCollider);
                victim   = vr.GetComponent<PlayerHealth>();
            }
            // vr == null 이면 벽에 막힌 것이다. 정상 차폐.
        }

        // --- 복원 ---
        _rewind?.SetHitboxesActive(true);
        foreach (var pr in rewound) pr.Restore();

        // --- 데미지 ---
        bool killed = false;
        if (hit && victim != null)
        {
            int dmg = headshot ? WeaponConfig.HeadDamage : WeaponConfig.BodyDamage;
            killed = victim.ApplyDamage(dmg, _health);
        }

        EmitCombat(input, serverTick, rttMs, shotIndex,
                   hit, headshot, killed, dist, victim, rewindSec);

        // 사수에게만 히트마커를 통보한다.
        if (hit)
            HitFeedbackClientRpc(headshot, killed,
                RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void HitFeedbackClientRpc(bool headshot, bool killed, RpcParams _)
    {
        OnHitConfirmed?.Invoke(headshot, killed);
    }

    /// <summary>UI 가 구독한다. (headshot, killed)</summary>
    public event System.Action<bool, bool> OnHitConfirmed;

    // -----------------------------------------------------------------
    //  텔레메트리
    // -----------------------------------------------------------------

    private void EmitCombat(
        InputPayload input, int serverTick, int rttMs, int shotIndex,
        bool hit, bool headshot, bool killed, float dist,
        PlayerHealth victim, float rewindSec)
    {
        var w = TelemetryWriter.Instance;
        if (w == null || !w.IsActive) return;

        // 이 시점의 이론적 반동 누적. 실제 조준각과의 차이가
        // W8 노리코일 탐지의 입력이 된다.
        Vector2 expected = WeaponConfig.GetAccumulatedRecoil(shotIndex);

        string type = killed ? "KILL" : (hit ? "HIT" : "FIRE");

        _sb.Clear();
        _sb.Append("{\"t\":\"combat\"");
        _sb.Append(",\"match_uid\":").Append(TJson.Str(w.MatchUid));
        _sb.Append(",\"player_uid\":").Append(
            TJson.Str(_telemetry != null ? _telemetry.PlayerUid : "unknown"));
        _sb.Append(",\"event_type\":").Append(TJson.Str(type));
        _sb.Append(",\"weapon_id\":\"rifle\"");
        _sb.Append(",\"server_tick\":").Append(serverTick.ToString(TJson.Inv));
        _sb.Append(",\"client_tick\":").Append(input.tick.ToString(TJson.Inv));
        _sb.Append(",\"ts\":").Append(TJson.Str(TJson.Now()));
        _sb.Append(",\"shot_index\":").Append(shotIndex.ToString(TJson.Inv));
        _sb.Append(",\"yaw\":").Append(TJson.F(input.yaw));
        _sb.Append(",\"pitch\":").Append(TJson.F(input.pitch));
        _sb.Append(",\"expected_recoil_pitch\":").Append(TJson.F(expected.y));
        _sb.Append(",\"is_headshot\":").Append(headshot ? "true" : "false");
        _sb.Append(",\"target_dist\":").Append(hit ? TJson.F(dist) : "null");
        _sb.Append(",\"rewind_ms\":").Append(
            Mathf.RoundToInt(rewindSec * 1000f).ToString(TJson.Inv));
        _sb.Append(",\"rtt_ms\":").Append(rttMs >= 0 ? rttMs.ToString(TJson.Inv) : "null");
        _sb.Append('}');

        w.Write(_sb.ToString());
    }
}
