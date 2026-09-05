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
//   2) 이동과 사격이 같은 틱 타임라인 위에 놓여 되감기 계산이 단순해진다.
//
//  ─────────────────────────────────────────────────────────────────
//  W7 Day 1 : 레이어 분리로 랙 보상 복구, V-FIRE-01 토큰 버킷
//  W7 Day 3 : 조준 오차 / 표적 식별 / V-LOS BlockedHit
//
//  (1) aim_error_deg 를 발사 시점에 계산한다.
//      20Hz 가시성 루프에서 가져오면 최대 50ms 묵은 값이라 플릭 사격에서
//      크게 어긋난다. FireHitscan 안에서는 이미 전원을 되감아 놓은
//      상태라 정확한 값이 추가 비용 없이 나온다.
//
//      ★ 빗나간 FIRE 에도 기록한다.
//        에임봇의 신호는 "맞췄다"가 아니라 "오차 분포가 비정상적으로
//        좁다"이다. 명중분만 모으면 W13 에서 이 feature 가 죽는다.
//
//  (2) target_uid 를 함께 싣는다. ingester 가 players.id 로 해석한다.
//      명중이면 피격자, 빗나갔으면 조준선에 가장 가까운 적이다.
//      빗나간 경우 차폐된 적은 후보에서 뺀다. 벽 뒤 적을 우연히 겨눈 것을
//      정밀 조준으로 집계하면 오차 분포가 오염된다.
//
//  (3) target_dist 를 빗나간 사격에도 기록한다.
//      aim_error_deg 는 각도라 거리 없이는 실제 빗나간 폭을 알 수 없다.
//      1도는 5m 에서 8.7cm, 50m 에서 87cm 다. 에임봇 판별에는
//      각도보다 미터 단위 오차(aim_error x dist)가 더 직접적이다.
//      ResolveAimTarget 이 이미 거리를 계산하므로 버리지 않고 내보낸다.
//
//  (4) V-LOS-01 / BlockedHit 이중 확인선.
//      되감은 월드에서 벽이 더 가까우면 히트가 성립하지 않으므로
//      이 검사는 원리상 발화하지 않는다. 그래도 넣는 이유는
//      레이어 마스크가 잘못 설정되면 조용히 뚫리기 때문이다.
//      W7 Day 1 에서 실제로 겪은 종류의 사고다.
//      정상이면 Day 5 측정에서 0건으로 남고, 그 0 자체가 근거가 된다.
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

    /// <summary>
    /// 이 각도 밖의 적은 "겨냥한 대상"으로 보지 않는다.
    /// 넓히면 아무 방향으로 쏴도 표적이 잡혀 오차 분포가 무의미해진다.
    /// </summary>
    private const float AimCandidateConeDeg = 30f;

    /// <summary>차폐 판정 시 벽 표면 근접 허용 오차.</summary>
    private const float OccludeMargin = 0.05f;

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

    private static readonly StringBuilder _sb = new StringBuilder(448);

    /// <summary>히트스캔 대상: 되감긴 히트박스 + 벽. 플레이어 본체는 제외.</summary>
    private int _raycastMask;

    /// <summary>차폐 판정 전용: 벽만.</summary>
    private int _worldMask;

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
    /// 본체가 마스크에 있으면 되감기가 사실상 무시된다. (W7 Day 1)
    /// </summary>
    private void BuildRaycastMask()
    {
        int world = LayerMask.NameToLayer(WorldLayerName);
        int hitbox = LayerMask.NameToLayer(WeaponConfig.HitboxLayerName);

        _worldMask = 0;
        _raycastMask = 0;

        if (world >= 0) { _worldMask = 1 << world; _raycastMask |= _worldMask; }
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
    public Vector2 ClientTryFire(int tick, bool firePressed)
    {
        if (!firePressed) return Vector2.zero;
        if (_health != null && _health.IsDead) return Vector2.zero;

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

    public void ServerProcessInput(InputPayload input, int serverTick, int rttMs)
    {
        if (!IsServer) return;
        if ((input.buttons & BTN_FIRE) == 0) return;
        if (_health != null && _health.IsDead) return;

        float now = Time.realtimeSinceStartup;

        // --- 연사 중단 판정 (서버 실시간 기준) ---
        // 클라 틱으로 판정하면 틱을 크게 점프시켜 매 발을 shotIndex=0 으로
        // 만들 수 있고, expected_recoil_pitch 가 항상 0 이 되어
        // W8 노리코일 탐지가 비교할 기준선을 잃는다.
        if (now - _lastFireRealtime > WeaponConfig.RecoilResetTicks / VFire.TickRate)
            _shotIndex = 0;

        // --- 게임플레이 게이트 (게임시간 기준) ---
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
        // 클라이언트가 보낸 위치는 쓰지 않는다.
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
        float hitDist = 0f;
        PlayerRewind victimRewind = null;
        PlayerHealth victim = null;

        if (Physics.Raycast(origin, dir, out RaycastHit rh,
                            WeaponConfig.MaxRange, _raycastMask,
                            QueryTriggerInteraction.Ignore))
        {
            hitDist = rh.distance;
            var vr = rh.collider.GetComponentInParent<PlayerRewind>();
            if (vr != null)
            {
                hit = true;
                headshot = (rh.collider == vr.HeadCollider);
                victimRewind = vr;
                victim = vr.GetComponent<PlayerHealth>();
            }
            // vr == null 이면 벽에 막힌 것이다. 정상 차폐.
        }

        // --- 조준 오차와 표적 (되감긴 상태에서 계산해야 한다) ---
        ResolveAimTarget(origin, dir, rewound, victimRewind,
                         out string targetUid, out float aimErrorDeg, out float aimDist);

        // --- V-LOS-01 / BlockedHit 이중 확인선 ---
        // 히트가 성립했는데 사이에 벽이 있으면 마스크 설정이 잘못된 것이다.
        if (hit && victimRewind != null)
        {
            if (hitDist > OccludeMargin &&
                Physics.Raycast(origin, dir, hitDist - OccludeMargin,
                                _worldMask, QueryTriggerInteraction.Ignore))
            {
                hit = false;
                victim = null;
                ViolationLogger.Report(
                    OwnerClientId, PlayerUid, VLos.CODE,
                    input.tick, VLos.Severity, "BlockedHit", rttMs);
            }
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

        // 명중이면 실제 피탄 거리, 빗나갔으면 표적까지의 거리.
        // 둘 다 없으면 -1 로 두어 NULL 로 나간다.
        float reportDist = hit ? hitDist : aimDist;

        EmitCombat(input, serverTick, rttMs, shotIndex,
                   hit, headshot, killed, reportDist, rewindSec,
                   targetUid, aimErrorDeg);

        if (hit)
            HitFeedbackClientRpc(headshot, killed,
                RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
    }

    /// <summary>
    /// 이 발사가 누구를 겨냥한 것인지, 그 오차각과 거리를 정한다.
    /// 되감기가 걸린 상태에서 호출해야 한다.
    ///
    /// 명중이면 피격자가 곧 표적이다.
    /// 빗나갔으면 조준선에 가장 가까운 적을 표적으로 본다. 다만
    /// 차폐된 적은 제외한다. 벽 뒤 적을 우연히 겨눈 것을 정밀 조준으로
    /// 집계하면 aim_error 분포가 오염된다.
    /// </summary>
    private void ResolveAimTarget(
        Vector3 origin, Vector3 dir,
        List<PlayerRewind> rewound, PlayerRewind victimRewind,
        out string targetUid, out float aimErrorDeg, out float targetDist)
    {
        targetUid = null;
        aimErrorDeg = -1f;
        targetDist = -1f;

        if (victimRewind != null)
        {
            Vector3 to = CenterOf(victimRewind) - origin;
            targetUid = UidOf(victimRewind);
            aimErrorDeg = Vector3.Angle(dir, to);
            targetDist = to.magnitude;
            return;
        }

        PlayerRewind best = null;
        float bestAngle = AimCandidateConeDeg;
        float bestLen = -1f;

        foreach (var pr in rewound)
        {
            var ph = pr.GetComponent<PlayerHealth>();
            if (ph != null && ph.IsDead) continue;

            Vector3 to = CenterOf(pr) - origin;
            float len = to.magnitude;
            if (len < 0.01f || len > WeaponConfig.MaxRange) continue;

            float ang = Vector3.Angle(dir, to);
            if (ang >= bestAngle) continue;

            // 차폐된 적은 후보에서 뺀다.
            if (Physics.Raycast(origin, to / len, len - OccludeMargin,
                                _worldMask, QueryTriggerInteraction.Ignore))
                continue;

            bestAngle = ang;
            bestLen = len;
            best = pr;
        }

        if (best != null)
        {
            targetUid = UidOf(best);
            aimErrorDeg = bestAngle;
            targetDist = bestLen;
        }
    }

    /// <summary>되감긴 상태의 몸통 중심. 히트박스가 실제로 놓인 위치다.</summary>
    private static Vector3 CenterOf(PlayerRewind pr)
        => pr.BodyCollider != null
         ? pr.BodyCollider.bounds.center
         : PlayerRewind.BodyCenterFrom(pr.transform.position);

    private static string UidOf(PlayerRewind pr)
    {
        var t = pr.GetComponent<PlayerTelemetry>();
        return t != null ? t.PlayerUid : null;
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
        bool hit, bool headshot, bool killed, float dist, float rewindSec,
        string targetUid, float aimErrorDeg)
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
        _sb.Append(",\"target_dist\":").Append(dist >= 0f ? TJson.F(dist) : "null");
        _sb.Append(",\"target_uid\":").Append(
            targetUid != null ? TJson.Str(targetUid) : "null");
        _sb.Append(",\"aim_error_deg\":").Append(
            aimErrorDeg >= 0f ? TJson.F(aimErrorDeg) : "null");
        _sb.Append(",\"rewind_ms\":").Append(
            Mathf.RoundToInt(rewindSec * 1000f).ToString(TJson.Inv));
        _sb.Append(",\"rtt_ms\":").Append(rttMs >= 0 ? rttMs.ToString(TJson.Inv) : "null");
        _sb.Append('}');

        w.Write(_sb.ToString());
    }
}