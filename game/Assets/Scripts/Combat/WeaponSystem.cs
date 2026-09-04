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
//
//  ─────────────────────────────────────────────────────────────────
//  W7 Day 1 변경 (2026-09-04)
//
//  (1) 레이캐스트 마스크에서 플레이어 본체를 제외했다.  ★ 중대 ★
//      기존 마스크는 Default + Hitbox 였고 플레이어 루트도 Default 였다.
//      PlayerRewind 는 별도 히트박스만 되감고 CharacterController 캡슐은
//      현재 위치에 그대로 둔다. 그 결과 레이가 되감은 히트박스보다
//      "현재 위치의 캡슐"에 먼저 맞았고, GetComponentInParent<PlayerRewind>()
//      가 같은 오브젝트라 non-null 이 되어 정상 명중으로 처리됐다.
//        → 랙 보상이 무력화되어 현재 위치로 판정
//        → 캡슐이 머리 히트박스를 감싸므로 헤드샷이 원리상 불가능
//      W6.5 실측에서 26히트 중 헤드샷 0건이 나온 원인이다.
//      플레이어 루트를 Player 레이어로 옮기고 마스크에서 뺀다.
//      히트스캔은 Hitbox 레이어(되감김) 와 World 레이어(벽) 만 본다.
//
//  (2) 반동 인덱스 리셋을 서버 실시간 기준으로 바꿨다.
//      클라 틱 기준이면 틱을 RecoilResetTicks 이상 점프시켜 매 발을
//      shotIndex=0 으로 만들 수 있고, expected_recoil_pitch 가 항상 0 이
//      되어 W8 노리코일 탐지의 기준선이 통째로 사라진다.
//
//  (3) V-FIRE-01 배선. 틱 간격 검사는 게임플레이 게이트로 남기고,
//      실시간 토큰 버킷을 위반 판정에 쓴다. 근거는 FireRateValidator.cs 참조.
// =====================================================================

using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class WeaponSystem : NetworkBehaviour
{
    private const byte BTN_FIRE = 1 << 1;

    /// <summary>맵 지형·벽이 놓인 레이어. 차폐 판정의 대상이다.</summary>
    private const string WorldLayerName = "Default";

    // --- 서버 상태 ---
    private int _lastFireTick = -1000;
    private float _lastFireRealtime = -999f;
    private int _shotIndex = 0;      // 연사 중 몇 번째 발인지 (반동 인덱스)

    private FireRateValidator _fireValidator;

    // --- 클라 상태 (반동 체감용) ---
    private int _clientLastFireTick = -1000;
    private int _clientShotIndex = 0;

    private PlayerHealth _health;
    private PlayerRewind _rewind;
    private PlayerTelemetry _telemetry;

    private static readonly StringBuilder _sb = new StringBuilder(384);

    /// <summary>레이캐스트 대상: 되감긴 히트박스 + 벽. 플레이어 본체는 제외.</summary>
    private int _raycastMask;

    private string PlayerUid =>
        _telemetry != null ? _telemetry.PlayerUid : "unknown";

    public override void OnNetworkSpawn()
    {
        _health = GetComponent<PlayerHealth>();
        _rewind = GetComponent<PlayerRewind>();
        _telemetry = GetComponent<PlayerTelemetry>();

        BuildRaycastMask();

        if (IsServer)
            _fireValidator = new FireRateValidator(
                Time.realtimeSinceStartup, WeaponConfig.FireIntervalTicks);
    }

    /// <summary>
    /// 히트스캔이 볼 레이어를 구성한다.
    ///
    /// 플레이어 본체(CharacterController)를 넣으면 안 된다.
    /// 본체는 서버의 현재 위치에 있고 히트박스는 과거로 되감겨 있으므로,
    /// 본체가 마스크에 있으면 되감기가 사실상 무시된다.
    /// </summary>
    private void BuildRaycastMask()
    {
        int world = LayerMask.NameToLayer(WorldLayerName);
        int hitbox = LayerMask.NameToLayer(WeaponConfig.HitboxLayerName);

        _raycastMask = 0;

        if (world >= 0) _raycastMask |= (1 << world);
        else Debug.LogError($"[WEAPON] '{WorldLayerName}' 레이어가 없다. 벽 차폐가 작동하지 않는다.");

        if (hitbox >= 0) _raycastMask |= (1 << hitbox);
        else Debug.LogError(
            $"[WEAPON] '{WeaponConfig.HitboxLayerName}' 레이어가 없다. " +
            "Project Settings > Tags and Layers 에서 만들어야 한다. " +
            "이 레이어가 없으면 되감기가 히트 판정에 전혀 반영되지 않는다.");
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

        float now = Time.realtimeSinceStartup;

        // --- 연사 중단 판정 (서버 실시간 기준) ---
        // 클라 틱으로 판정하면 틱을 크게 점프시켜 매 발을 shotIndex=0 으로
        // 만들 수 있다. 그러면 expected_recoil_pitch 가 항상 0 이 되어
        // W8 노리코일 탐지가 비교할 기준선을 잃는다.
        if (now - _lastFireRealtime > WeaponConfig.RecoilResetTicks / VFire.TickRate)
            _shotIndex = 0;

        // --- 게임플레이 게이트 (게임시간 기준) ---
        // 지터로 입력이 몰려 도착해도 게임시간만큼의 발수가 정상적으로 나간다.
        if (input.tick - _lastFireTick < WeaponConfig.FireIntervalTicks) return;

        // --- V-FIRE-01 : 실시간 상한 ---
        if (!_fireValidator.TryFire(now, out var reason))
        {
            if (reason != FireRejectReason.None)
                ViolationLogger.Report(
                    OwnerClientId, PlayerUid, VFire.CODE,
                    input.tick, VFire.Severity, reason.ToString(), rttMs);
            return;
        }

        _lastFireTick = input.tick;
        _lastFireRealtime = now;

        int shotIndex = _shotIndex;
        _shotIndex++;

        FireHitscan(input, serverTick, rttMs, shotIndex);
    }

    /// <summary>리스폰 시 호출. 유예를 다시 주고 반동 인덱스를 초기화한다.</summary>
    public void ServerOnRespawn()
    {
        if (!IsServer) return;
        float now = Time.realtimeSinceStartup;
        _fireValidator?.ResetGrace(now);
        _shotIndex = 0;
        _lastFireTick = -1000;
        _lastFireRealtime = -999f;
    }

    private void FireHitscan(InputPayload input, int serverTick, int rttMs, int shotIndex)
    {
        // --- 조준 원점과 방향 ---
        // 클라이언트가 보낸 위치는 쓰지 않는다. 서버의 권위 위치에서
        // 눈 오프셋을 더해 재구성한다.
        Vector3 origin = transform.position + WeaponConfig.EyeOffset;
        Vector3 dir = Quaternion.Euler(input.pitch, input.yaw, 0f) * Vector3.forward;

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

        bool hit = false;
        bool headshot = false;
        float dist = 0f;
        PlayerHealth victim = null;

        if (Physics.Raycast(origin, dir, out RaycastHit rh,
                            WeaponConfig.MaxRange, _raycastMask,
                            QueryTriggerInteraction.Ignore))
        {
            dist = rh.distance;
            var vr = rh.collider.GetComponentInParent<PlayerRewind>();
            if (vr != null)
            {
                hit = true;
                headshot = (rh.collider == vr.HeadCollider);
                victim = vr.GetComponent<PlayerHealth>();
            }
            // vr == null 이면 벽에 막힌 것이다. 정상 차폐.
            //
            // 마스크에 플레이어 본체가 없으므로 여기 걸리는 것은
            // 되감긴 히트박스이거나 World 지형뿐이다.
            // V-LOS-01 의 BlockedHit 은 이 경로가 정상 동작하는지를
            // 이중으로 확인하는 용도로 Day 2 에서 붙인다.
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
        _sb.Append(",\"player_uid\":").Append(TJson.Str(PlayerUid));
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