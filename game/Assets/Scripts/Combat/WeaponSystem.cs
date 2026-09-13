// =====================================================================
//  WeaponSystem.cs
//  경로: game/Assets/Scripts/Combat/WeaponSystem.cs
//
//  사격. PlayerCharacter 프리팹에 붙인다.
//
//  구조
//   클라이언트: 반동을 시야에 적용한다(체감용). 탄약을 예측한다.
//   서버      : 발사 판정, 탄약, 되감기, 히트스캔, 데미지, 텔레메트리.
//
//  발사·재장전 신호는 InputPayload.buttons 의 비트로 온다.
//  별도 RPC 를 만들지 않는 이유는 두 가지다.
//   1) V-MOVE-01 의 토큰 버킷이 그대로 적용된다.
//   2) 이동·사격·재장전이 같은 틱 타임라인 위에 놓여 순서가 확정된다.
//
//  ─────────────────────────────────────────────────────────────────
//  W7 Day 1 : 레이어 분리로 랙 보상 복구, V-FIRE-01 토큰 버킷
//  W7 Day 3 : 조준 오차 / 표적 식별 / V-LOS BlockedHit
//  W7 Day 4 : spot_event_id 기록, V-TIME-01 배선
//  W7 Day 5 : shotIndex 를 V-TIME-01 에 전달, OnClientFired 이벤트
//  W8 Day 1 : 반동 인덱스 축 불일치 계측 + 발사 경로 카운터
//  W8 Day 2 : V-RECOIL-01 배선 (고정 빈도 지표)
//  W8 Day 3 : 탄약 / 재장전  ← 이번 변경
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 3 : 탄약 게이트의 순서가 중요하다 ★
//
//   게이트 순서는 이렇게 고정한다.
//
//     1. 게임플레이 간격 게이트 (클라 틱)
//     2. 재장전 중인가            ← 탄약 게이트
//     3. 탄약이 남아 있는가        ← 탄약 게이트
//     4. V-FIRE-01 토큰 버킷
//     5. 승인
//
//   탄약 게이트가 V-FIRE 뒤로 가면 오탐이 터진다.
//
//   GatherInput 은 마우스를 누르고 있는 동안 매 틱 BTN_FIRE 를 보낸다.
//   탄약이 0이거나 재장전 중이면 _lastFireTick 이 전진하지 않으므로
//   gapTicks 가 계속 커져 간격 게이트를 매번 통과한다. 그 요청이
//   V-FIRE 까지 도달하면 초당 60개씩 토큰을 태운다. 충전은 10/s 라
//   즉시 위반이 난다.
//
//   W7 Day 5 의 V-MOVE 183건 연쇄와 같은 구조다. 거부 경로가 상태를
//   전진시키지 않으면 정상 입력이 무한히 재시도되어 다음 검증기를
//   폭격한다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 3 : 탄약 0 상태의 발사 요청 ★
//
//   정상 클라이언트는 탄약이 0이면 BTN_FIRE 를 보내도 ClientTryFire 가
//   막으므로, 서버가 "탄약 0인데 발사 게이트까지 도달한 요청"을 보는
//   일은 예측 오차 범위(한두 발) 외에는 없어야 한다.
//
//   이 값이 크면 클라이언트 변조 신호다. 다만 표본이 없으므로 오늘은
//   카운터만 세고 위반 코드는 만들지 않는다. 실측 후 W9 에서
//   V-AMMO-01 로 승격할지 판단한다.
//
//   ※ 자동 재장전을 넣지 않은 이유가 여기 있다. 자동으로 채우면
//     "탄약 0에서의 발사 요청"이 정상 플레이에서도 섞여 들어와
//     이 카운터가 무의미해진다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 3 : 재장전과 반동 인덱스 ★
//
//   재장전하면 _shotIndex 를 0 으로 되돌린다. 새 탄창은 새 스프레이라는
//   것이 일반적인 FPS 규칙이고, 클라이언트도 같은 시점에 리셋한다.
//
//   V-RECOIL-01 은 따로 손댈 필요가 없다. 재장전 후 첫 발이
//   shotIndex == 0 이므로 기존 버스트 경계 로직이 _lastPitch 를 알아서
//   재설정한다. 판정 창은 버스트를 넘어 이어지도록 설계돼 있으므로
//   재장전 중에도 유지되는 것이 맞다.
//
//   ※ 탄창 30발에서 램프 8발을 빼면 탄창당 표본이 22발이다.
//     창 40발은 두 탄창이면 찬다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 2 : V-RECOIL-01 ★
//
//   지표는 "두 발 사이에 조준점이 전혀 안 움직인 비율"이다.
//   창 40발 중 24발(60%) 이상이면 보고한다.
//   실측 정상 4.6~9.0% / 노리코일 64.8~93.3%.
//
//   ※ 1차 설계였던 표본 표준편차는 조준을 유지한 노리코일에서
//     무너졌다(match 72). 실패 기록과 임계 근거는
//     RecoilValidator.cs 상단 참조.
//
//   차단은 하지 않는다. 관리자가 대시보드를 보고 판단한다(B안).
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 1 : 왜 fire_gap_ticks 와 fire_gap_ms 를 둘 다 남기는가 ★
//
//   연사 중단(= 반동 인덱스 리셋) 판정 기준이 클라와 서버에서 다르다.
//
//     클라 _clientShotIndex : tick - _clientLastFireTick > RecoilResetTicks
//     서버 _shotIndex       : now  - _lastFireRealtime  > RecoilResetTicks/60
//
//   서버가 실시간을 쓰는 것은 의도된 설계다. 틱으로 판정하면 틱을 크게
//   점프시켜 매 발을 shotIndex=0 으로 만들 수 있고, V-RECOIL-01 이
//   램프 구간만 보게 되어 판정 표본을 전부 잃는다.
//
//   실측 불일치율은 발사 기준 0.28%, 버스트 기준 1.4% 였다(match 57).
//   판정은 SQL 에서 한다. 서버가 미리 접어서 저장하지 않는 이유는
//   RecoilResetTicks 를 조정할 때 과거 데이터를 다시 해석할 수 있어야
//   하기 때문이다. 두 값 모두 첫 발에서는 null 이다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 1 : 발사 경로 카운터 ★
//
//   거부된 발사는 combat_events 에 아무 행도 남기지 않는다. 특히
//   FireRejectReason.None 으로 거부되는 경로는 ViolationLogger 에도
//   안 남아서 완전히 보이지 않았다.
//
//   행을 더 만들지 않고 카운터로 세서 리스폰·디스폰 시 로그로 남긴다.
//   Promtail → Loki 로 들어가므로 스키마 변경 없이 조회된다.
//
//   ※ FireIntervalTicks=6 은 초당 10발이고 V-FIRE 토큰 버킷 충전도
//     10/s 다. 헤드룸이 0이라 V-MOVE 에서 겪은 연쇄 구조와 같다.
//     capacity 4 로 버티는 중이므로 violationRejected 값을 보고
//     충전율 상향을 판단한다. 올리면 rapidfire 탐지 임계도 같이
//     올라가는 트레이드오프가 있다.
//
//  ─────────────────────────────────────────────────────────────────
//  aim_error_deg 를 발사 시점에 계산하는 이유
//   20Hz 가시성 루프에서 가져오면 최대 50ms 묵은 값이라 플릭 사격에서
//   크게 어긋난다. FireHitscan 안에서는 이미 전원을 되감아 놓은
//   상태라 정확한 값이 추가 비용 없이 나온다.
//
//   ★ 빗나간 FIRE 에도 기록한다.
//     에임봇의 신호는 "맞췄다"가 아니라 "오차 분포가 비정상적으로
//     좁다"이다. 명중분만 모으면 W13 에서 이 feature 가 죽는다.
//
//  target_dist 를 빗나간 사격에도 채우는 이유
//   aim_error_deg 는 각도라 거리 없이는 실제 빗나간 폭을 알 수 없다.
//   1도는 5m 에서 8.7cm, 50m 에서 87cm 다.
//
//  spot_event_id
//   이 발사가 어느 SPOT 에서 이어진 것인지 이어 준다.
//   ※ 조인은 반드시 (match_id, spot_event_id) 복합키로 한다.
//     spot_event_id 는 서버 프로세스 스코프라 재시작하면 1부터
//     다시 시작하며, 단독 조인은 세션 간 오염을 만든다.
//
//  V-LOS-01 / BlockedHit
//   되감은 월드에서 벽이 더 가까우면 히트가 성립하지 않으므로
//   원리상 발화하지 않는다. 그래도 넣는 이유는 레이어 마스크가
//   잘못 설정되면 조용히 뚫리기 때문이다. W7 Day 1 에서 실제로 겪었다.
//   정상이면 0건으로 남고, 그 0 자체가 근거가 된다.
// =====================================================================

using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class WeaponSystem : NetworkBehaviour
{
    private const byte BTN_FIRE = 1 << 1;
    private const byte BTN_RELOAD = 1 << 4;

    /// <summary>맵 지형·벽이 놓인 레이어. 차폐 판정의 대상이다.</summary>
    private const string WorldLayerName = "Default";

    /// <summary>
    /// 이 각도 밖의 적은 "겨냥한 대상"으로 보지 않는다.
    /// 넓히면 아무 방향으로 쏴도 표적이 잡혀 오차 분포가 무의미해진다.
    /// </summary>
    private const float AimCandidateConeDeg = 30f;

    /// <summary>차폐 판정 시 벽 표면 근접 허용 오차.</summary>
    private const float OccludeMargin = 0.05f;

    // -----------------------------------------------------------------
    //  탄약 (서버 권위, 클라 읽기 전용)
    // -----------------------------------------------------------------
    //
    //  NetworkVariable 로 노출해 HUD 가 구독한다. 쓰기 권한은 서버에만
    //  준다. 클라이언트가 값을 바꿔도 서버 판정에는 영향이 없다.

    private readonly NetworkVariable<int> _ammo = new NetworkVariable<int>(
        WeaponConfig.MagSize,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> _reserve = new NetworkVariable<int>(
        WeaponConfig.ReserveAmmo,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> _reloading = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>탄창 잔탄. HUD 가 읽는다.</summary>
    public int Ammo => _ammo.Value;

    /// <summary>여분 탄약. HUD 가 읽는다.</summary>
    public int Reserve => _reserve.Value;

    /// <summary>재장전 중인가. HUD 가 읽는다.</summary>
    public bool IsReloading => _reloading.Value;

    /// <summary>재장전이 끝나는 서버 실시간. 음수면 재장전 중이 아니다.</summary>
    private float _reloadEndsAt = -1f;

    // --- 서버 상태 ---
    private int _lastFireTick = -1000;
    private float _lastFireRealtime = -999f;
    private int _shotIndex = 0;      // 연사 중 몇 번째 발인지 (반동 인덱스)

    // --- 서버 발사 경로 카운터 (W8 Day 1/3) ---
    // 거부된 발사는 combat_events 에 남지 않으므로 여기서 센다.
    private int _fireAccepted;
    private int _fireGateRejected;       // 게임플레이 발사 간격 게이트
    private int _fireReloadRejected;     // 재장전 중 발사 요청
    private int _fireNoAmmoRejected;     // 탄약 0 상태 발사 요청 (변조 신호 후보)
    private int _fireViolationRejected;  // V-FIRE-01, 사유 있음
    private int _fireSilentRejected;     // V-FIRE-01, 사유 None (흔적 없던 경로)
    private int _reloadAccepted;
    private int _reloadIgnored;          // 이미 가득 / 이미 재장전 중 / 여분 없음

    private FireRateValidator _fireValidator;
    private ReactionTimeValidator _reaction;
    private RecoilValidator _recoil;

    // --- 클라 상태 (반동·탄약 예측용) ---
    private int _clientLastFireTick = -1000;
    private int _clientShotIndex = 0;
    private int _clientAmmo = WeaponConfig.MagSize;
    private float _clientReloadEndsAt = -1f;

    private PlayerHealth _health;
    private PlayerRewind _rewind;
    private PlayerTelemetry _telemetry;

    private static readonly StringBuilder _sb = new StringBuilder(520);

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
        {
            _fireValidator = new FireRateValidator(
                Time.realtimeSinceStartup, WeaponConfig.FireIntervalTicks);
            _reaction = new ReactionTimeValidator();
            _recoil = new RecoilValidator();
        }

        if (IsOwner)
        {
            _clientAmmo = WeaponConfig.MagSize;
            _clientReloadEndsAt = -1f;
        }
    }

    /// <summary>
    /// 세션 최종 계측을 남긴다.
    ///
    /// 위반 건수만으로는 해석할 수 없다. 측정이 몇 번 일어났는지(분모)를
    /// 모르면 "위반 1건"이 오탐률 0.5% 인지 33% 인지 구분되지 않는다.
    /// V-TIME 에서 이걸로 한 번 데였다.
    /// </summary>
    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            Debug.Log($"[FIRE] final uid={PlayerUid} {FireStatsLine()}");
            if (_reaction != null)
                Debug.Log($"[VTIME] final uid={PlayerUid} {_reaction.StatsLine()}");
            if (_recoil != null)
                Debug.Log($"[VRECOIL] final uid={PlayerUid} {_recoil.StatsLine()}");
        }

        base.OnNetworkDespawn();
    }

    private string FireStatsLine()
        => $"accepted={_fireAccepted} gateRejected={_fireGateRejected} " +
           $"reloadRejected={_fireReloadRejected} " +
           $"noAmmoRejected={_fireNoAmmoRejected} " +
           $"violationRejected={_fireViolationRejected} " +
           $"silentRejected={_fireSilentRejected} " +
           $"reloads={_reloadAccepted} reloadIgnored={_reloadIgnored} " +
           $"ammo={_ammo.Value}/{_reserve.Value}";

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
    //  클라이언트: 반동과 탄약 예측
    // -----------------------------------------------------------------

    /// <summary>
    /// 소유 클라이언트가 발사 입력을 만들 때 호출한다.
    /// 반동만큼 시야를 밀어 올리고 탄약을 하나 소비한다.
    ///
    /// 탄약을 클라에서도 세는 이유는 두 가지다.
    ///   1) 탄약이 0인데 반동만 계속 올라가는 것을 막는다.
    ///   2) 서버가 거부할 발사에 BTN_FIRE 를 계속 보내면 V-FIRE 토큰을
    ///      태운다. 클라가 먼저 멈추는 것이 옳다.
    ///
    /// 이 값은 예측일 뿐이다. 판정은 전적으로 서버가 한다.
    ///
    /// ※ 여기의 반동 리셋은 클라 틱 축이고 서버는 실시간 축이다.
    ///   불일치율은 실측 0.28%(발사 기준). 파일 상단 참조.
    /// </summary>
    public Vector2 ClientTryFire(int tick, bool firePressed)
    {
        if (!firePressed) return Vector2.zero;
        if (_health != null && _health.IsDead) return Vector2.zero;

        // 재장전 중에는 발사가 성립하지 않는다.
        float now = Time.realtimeSinceStartup;
        if (_clientReloadEndsAt > 0f)
        {
            if (now < _clientReloadEndsAt) return Vector2.zero;
            FinishClientReload();
        }

        if (_clientAmmo <= 0) return Vector2.zero;

        if (tick - _clientLastFireTick > WeaponConfig.RecoilResetTicks)
            _clientShotIndex = 0;

        if (tick - _clientLastFireTick < WeaponConfig.FireIntervalTicks)
            return Vector2.zero;

        _clientLastFireTick = tick;
        _clientAmmo--;

        Vector2 recoil = WeaponConfig.GetRecoil(_clientShotIndex);
        OnClientFired?.Invoke(_clientShotIndex);   // 증가 전 = 이번 발의 인덱스
        _clientShotIndex++;
        return recoil;
    }

    /// <summary>
    /// 소유 클라이언트가 재장전 입력을 만들 때 호출한다.
    /// 예측만 한다. 실제 탄약은 서버가 채운다.
    /// </summary>
    /// <returns>이번 틱에 재장전이 시작됐으면 true (이펙트·사운드용)</returns>
    public bool ClientTryReload(bool reloadPressed)
    {
        if (!reloadPressed) return false;
        if (_health != null && _health.IsDead) return false;
        if (_clientReloadEndsAt > 0f) return false;              // 이미 재장전 중
        if (_clientAmmo >= WeaponConfig.MagSize) return false;   // 이미 가득
        if (_reserve.Value <= 0) return false;                   // 여분 없음

        _clientReloadEndsAt = Time.realtimeSinceStartup + WeaponConfig.ReloadSec;
        OnClientReloadStarted?.Invoke();
        return true;
    }

    /// <summary>클라 예측 탄창을 채운다. 여분 차감은 서버 값을 따른다.</summary>
    private void FinishClientReload()
    {
        _clientReloadEndsAt = -1f;

        int need = WeaponConfig.MagSize - _clientAmmo;
        int take = Mathf.Min(need, _reserve.Value);
        _clientAmmo += take;

        // 새 탄창은 새 스프레이. 서버도 같은 시점에 리셋한다.
        _clientShotIndex = 0;
        _clientLastFireTick = -1000;
    }

    /// <summary>클라 예측 상태를 서버 값으로 되돌린다. 리스폰 시 호출.</summary>
    public void ClientResetAmmo()
    {
        _clientAmmo = WeaponConfig.MagSize;
        _clientReloadEndsAt = -1f;
        _clientShotIndex = 0;
        _clientLastFireTick = -1000;
    }

    // -----------------------------------------------------------------
    //  서버: 발사 처리
    // -----------------------------------------------------------------

    public void ServerProcessInput(InputPayload input, int serverTick, int rttMs)
    {
        if (!IsServer) return;
        if (_health != null && _health.IsDead) return;

        float now = Time.realtimeSinceStartup;

        // --- 재장전 완료 처리 ---
        // 발사 판정보다 먼저 한다. 재장전이 끝난 틱의 발사는 유효하다.
        if (_reloadEndsAt > 0f && now >= _reloadEndsAt)
            FinishServerReload();

        // --- 재장전 요청 ---
        if ((input.buttons & BTN_RELOAD) != 0)
            ServerTryReload(now);

        if ((input.buttons & BTN_FIRE) == 0) return;

        // --- 두 축의 간격을 리셋 판정 이전에 붙잡는다 (W8 Day 1 계측) ---
        // 첫 발은 기준이 없으므로 -1 로 두고 텔레메트리에서 null 로 나간다.
        int gapTicks = input.tick - _lastFireTick;
        int reportGapTicks = _lastFireTick < 0 ? -1 : gapTicks;
        int reportGapMs = _lastFireRealtime < 0f
                        ? -1
                        : Mathf.RoundToInt((now - _lastFireRealtime) * 1000f);

        // --- 연사 중단 판정 (서버 실시간 기준) ---
        if (now - _lastFireRealtime > WeaponConfig.RecoilResetTicks / VFire.TickRate)
            _shotIndex = 0;

        // --- 게이트 1 : 게임플레이 발사 간격 (클라 틱 기준) ---
        if (gapTicks < WeaponConfig.FireIntervalTicks)
        {
            _fireGateRejected++;
            return;
        }

        // --- 게이트 2 : 재장전 중 ---
        // ★ 반드시 V-FIRE 앞에 둔다. 파일 상단의 순서 설명 참조. ★
        if (_reloadEndsAt > 0f)
        {
            _fireReloadRejected++;
            return;
        }

        // --- 게이트 3 : 탄약 ---
        // ★ 반드시 V-FIRE 앞에 둔다. ★
        // 이 카운터가 크면 클라이언트 변조 신호다. 정상 클라이언트는
        // ClientTryFire 가 먼저 막으므로 예측 오차 범위를 넘지 않는다.
        if (_ammo.Value <= 0)
        {
            _fireNoAmmoRejected++;
            return;
        }

        // --- 게이트 4 : V-FIRE-01 실시간 상한 ---
        if (!_fireValidator.TryFire(now, out var reason))
        {
            if (reason != FireRejectReason.None)
            {
                _fireViolationRejected++;
                ViolationLogger.Report(
                    OwnerClientId, PlayerUid, VFire.CODE,
                    input.tick, VFire.Severity, reason.ToString(), rttMs);
            }
            else
            {
                // 사유 없는 거부. 지금까지 어디에도 남지 않던 경로다.
                _fireSilentRejected++;
            }
            return;
        }

        // --- 승인 ---
        _fireAccepted++;
        _ammo.Value--;
        _lastFireTick = input.tick;
        _lastFireRealtime = now;

        int shotIndex = _shotIndex;
        _shotIndex++;

        // --- V-RECOIL-01 : 노리코일 ---
        // 조준각만 쓰므로 되감기 이전에 판정한다. 히트스캔 경로와 무관하다.
        // 램프 구간(shotIndex < RecoilRampShots)은 표본에서 제외되고,
        // 창 40발이 차야 평가한다. 한 번 걸린 뒤에는 고정 수가 회복될
        // 때까지 다시 보고하지 않는다(히스테리시스).
        if (_recoil != null &&
            _recoil.TryEvaluate(shotIndex, input.pitch, reportGapTicks, out float ratio))
        {
            Debug.Log($"[VRECOIL] 위반 uid={PlayerUid} ratio={ratio:F3} " +
                      $"고정={_recoil.CurrentFrozen}/{_recoil.CurrentWindow} " +
                      $"shotIndex={shotIndex} tick={input.tick}");

            ViolationLogger.Report(
                clientId: OwnerClientId,
                playerUid: PlayerUid,
                code: VRecoil.CODE,
                tick: input.tick,
                severity: VRecoil.Severity,
                detail: "FrozenAim",
                rttMs: rttMs);
        }

        FireHitscan(input, serverTick, rttMs, shotIndex, reportGapTicks, reportGapMs);
    }

    // -----------------------------------------------------------------
    //  서버: 재장전
    // -----------------------------------------------------------------

    /// <summary>
    /// 재장전 요청을 처리한다.
    ///
    /// 거절 조건 세 가지
    ///   이미 재장전 중   : R 연타로 2초 잠금을 반복 거는 것을 막는다
    ///   탄창이 가득      : 같은 이유. 자해 수단이 되면 안 된다
    ///   여분 없음        : 채울 탄약이 없다
    ///
    /// 시간은 서버 실시간으로 잰다. 클라 틱으로 재면 틱을 부풀려
    /// 즉시 재장전할 수 있다. V-FIRE 가 실시간을 쓰는 것과 같은 이유다.
    /// </summary>
    private void ServerTryReload(float now)
    {
        if (_reloadEndsAt > 0f ||
            _ammo.Value >= WeaponConfig.MagSize ||
            _reserve.Value <= 0)
        {
            _reloadIgnored++;
            return;
        }

        _reloadAccepted++;
        _reloadEndsAt = now + WeaponConfig.ReloadSec;
        _reloading.Value = true;
    }

    /// <summary>재장전을 완료하고 반동 인덱스를 초기화한다.</summary>
    private void FinishServerReload()
    {
        _reloadEndsAt = -1f;
        _reloading.Value = false;

        int need = WeaponConfig.MagSize - _ammo.Value;
        int take = Mathf.Min(need, _reserve.Value);

        _ammo.Value += take;
        _reserve.Value -= take;

        // 새 탄창은 새 스프레이.
        //
        // V-RECOIL-01 은 따로 손대지 않는다. 다음 발이 shotIndex == 0 이라
        // 버스트 경계 로직이 _lastPitch 를 알아서 재설정한다. 판정 창은
        // 버스트를 넘어 이어지도록 설계돼 있으므로 유지되는 것이 맞다.
        _shotIndex = 0;
        _lastFireTick = -1000;
        _lastFireRealtime = -999f;
    }

    /// <summary>리스폰 시 호출. 유예를 다시 주고 상태를 초기화한다.</summary>
    public void ServerOnRespawn()
    {
        if (!IsServer) return;
        float now = Time.realtimeSinceStartup;

        _fireValidator?.ResetGrace(now);

        // 표적별 교전 상태만 버린다. _history 와 누적 카운터는 유지한다.
        // 매치 단위 누적이라야 RepeatLimit(최근 20회 중 5회) 판정이
        // 성립하고, W13 Trust Score 의 입력으로도 쓸 수 있다.
        if (_reaction != null)
        {
            Debug.Log($"[VTIME] respawn uid={PlayerUid} {_reaction.StatsLine()}");
            _reaction.ResetForRespawn();
        }

        // 리스폰은 조준각의 불연속점이다. 창과 기준 pitch 만 버리고
        // 누적 계측은 유지한다.
        if (_recoil != null)
        {
            Debug.Log($"[VRECOIL] respawn uid={PlayerUid} {_recoil.StatsLine()}");
            _recoil.ResetForRespawn();
        }

        // 탄약 전량 복구. RespawnDelaySec(3초) > ReloadSec(2초) 이므로
        // 사망 중 진행되던 재장전은 버려도 손해가 없다.
        _ammo.Value = WeaponConfig.MagSize;
        _reserve.Value = WeaponConfig.ReserveAmmo;
        _reloadEndsAt = -1f;
        _reloading.Value = false;

        _shotIndex = 0;
        _lastFireTick = -1000;
        _lastFireRealtime = -999f;

        ResetAmmoClientRpc(RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ResetAmmoClientRpc(RpcParams _)
    {
        ClientResetAmmo();
    }

    private void FireHitscan(InputPayload input, int serverTick, int rttMs,
                            int shotIndex, int gapTicks, int gapMs)
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

        float nowRt = Time.realtimeSinceStartup;
        float targetTime = nowRt - rewindSec;

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
        PlayerRewind aimTarget = ResolveAimTarget(
            origin, dir, rewound, victimRewind,
            out float aimErrorDeg, out float aimDist);

        // --- V-LOS-01 / BlockedHit 이중 확인선 ---
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

        // --- V-TIME-01 : 반응시간 ---
        // shotIndex 를 넘긴다. 연사 도중 발사는 반응이 아니다. (W7 Day 5)
        long spotId = ResolveSpotAndCheckReaction(
            aimTarget, nowRt, rttMs, input.tick, shotIndex);

        // 명중이면 실제 피탄 거리, 빗나갔으면 표적까지의 거리.
        float reportDist = hit ? hitDist : aimDist;

        EmitCombat(input, serverTick, rttMs, shotIndex,
                   hit, headshot, killed, reportDist, rewindSec,
                   aimTarget != null ? UidOf(aimTarget) : null,
                   aimErrorDeg, spotId, gapTicks, gapMs);

        if (hit)
            HitFeedbackClientRpc(headshot, killed,
                RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
    }

    /// <summary>
    /// 이 발사가 어느 SPOT 에서 이어진 것인지 찾고, 필요하면 V-TIME-01 을 기록한다.
    ///
    /// SPOT 하나당 최초 1발만 잰다. 여기에 더해 W7 Day 5 실측 결과로
    /// 두 게이트가 추가됐다 (ReactionTimeValidator 참조).
    ///
    ///   - shotIndex > 0  : 트리거를 이미 당기고 있었으므로 반응이 아니다.
    ///   - 3초 내 재발사   : 교전 중이던 상대의 재등장은 첫 조우가 아니다.
    ///
    /// spotId 는 게이트에 걸려도 그대로 반환한다. 텔레메트리에는 남겨
    /// SQL 로 사후 분석할 수 있어야 하기 때문이다.
    /// </summary>
    /// <returns>이어진 SPOT id. 없으면 0.</returns>
    private long ResolveSpotAndCheckReaction(
        PlayerRewind aimTarget, float nowRt, int rttMs, int clientTick, int shotIndex)
    {
        if (aimTarget == null) return 0;

        var vs = VisibilitySystem.Instance;
        if (vs == null) return 0;

        ulong targetId = aimTarget.OwnerClientId;
        if (!vs.TryGetSpot(OwnerClientId, targetId, out float spotTime, out long spotId))
            return 0;

        if (_reaction != null &&
            _reaction.TryMeasure(targetId, spotId, shotIndex, nowRt, spotTime, rttMs,
                                 out int adjustedMs, out int fastCount) &&
            ReactionTimeValidator.IsImpossible(adjustedMs))
        {
            bool repeated = fastCount >= VTime.RepeatLimit;
            ViolationLogger.Report(
                clientId: OwnerClientId,
                playerUid: PlayerUid,
                code: VTime.CODE,
                tick: clientTick,
                severity: repeated ? VTime.SeverityRepeat : VTime.SeveritySingle,
                detail: repeated ? "FastReactionRepeated" : "FastReaction",
                rttMs: rttMs);
        }

        return spotId;
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
    private PlayerRewind ResolveAimTarget(
        Vector3 origin, Vector3 dir,
        List<PlayerRewind> rewound, PlayerRewind victimRewind,
        out float aimErrorDeg, out float targetDist)
    {
        aimErrorDeg = -1f;
        targetDist = -1f;

        if (victimRewind != null)
        {
            Vector3 to = CenterOf(victimRewind) - origin;
            aimErrorDeg = Vector3.Angle(dir, to);
            targetDist = to.magnitude;
            return victimRewind;
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
            aimErrorDeg = bestAngle;
            targetDist = bestLen;
        }
        return best;
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

    /// <summary>
    /// 소유 클라이언트에서 발사가 성립한 순간. 인자는 이번 발의 shotIndex.
    /// 사운드·이펙트가 구독한다. 순수 클라이언트 표현이므로 서버 판정과 무관하다.
    ///
    /// ClientTryFire 의 반환값(반동 벡터)으로 발사를 감지하면 안 된다.
    /// 패턴 첫 발이 (0,0)이면 발사했는데도 zero 가 나온다.
    /// </summary>
    public event System.Action<int> OnClientFired;

    /// <summary>
    /// 소유 클라이언트에서 재장전이 시작된 순간. 사운드·애니메이션이 구독한다.
    /// 클라 예측이므로 서버가 거절하면 화면과 실제가 잠시 어긋날 수 있다.
    /// </summary>
    public event System.Action OnClientReloadStarted;

    // -----------------------------------------------------------------
    //  텔레메트리
    // -----------------------------------------------------------------

    private void EmitCombat(
        InputPayload input, int serverTick, int rttMs, int shotIndex,
        bool hit, bool headshot, bool killed, float dist, float rewindSec,
        string targetUid, float aimErrorDeg, long spotId,
        int gapTicks, int gapMs)
    {
        var w = TelemetryWriter.Instance;
        if (w == null || !w.IsActive) return;

        // 이 시점의 이론적 반동 누적.
        //
        // ※ 이 값은 0~shotIndex-1 의 누적이고, input.pitch 에는 이미
        //   GetRecoil(shotIndex) 가 반영돼 있다. 두 값을 그대로 차분하면
        //   한 칸 어긋나므로, 분석 쿼리는 shot_index 에서 반동을 직접
        //   유도해야 한다. 서버 판정(RecoilValidator)은 pitch 차분만
        //   쓰므로 이 어긋남과 무관하다.
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
        _sb.Append(",\"spot_event_id\":").Append(
            spotId > 0 ? spotId.ToString(TJson.Inv) : "null");

        // 반동 인덱스 리셋 판정의 두 축. 첫 발은 기준이 없으므로 null.
        _sb.Append(",\"fire_gap_ticks\":").Append(
            gapTicks >= 0 ? gapTicks.ToString(TJson.Inv) : "null");
        _sb.Append(",\"fire_gap_ms\":").Append(
            gapMs >= 0 ? gapMs.ToString(TJson.Inv) : "null");

        _sb.Append(",\"rewind_ms\":").Append(
            Mathf.RoundToInt(rewindSec * 1000f).ToString(TJson.Inv));
        _sb.Append(",\"rtt_ms\":").Append(rttMs >= 0 ? rttMs.ToString(TJson.Inv) : "null");
        _sb.Append('}');

        w.Write(_sb.ToString());
    }
}