// =====================================================================
//  PlayerRewind.cs
//  경로: game/Assets/Scripts/Combat/PlayerRewind.cs
//
//  Lag Compensation의 대상 쪽 컴포넌트. 서버에서만 동작한다.
//  PlayerCharacter 프리팹에 붙인다.
//
//  하는 일 네 가지
//   1) 히트박스 생성 — Body/Head 콜라이더를 코드로 만든다.
//      프리팹을 손대지 않고, 클라이언트에는 만들지 않는다.
//   2) 위치 이력 기록 — 매 서버 틱마다 (실시간, 위치, yaw)를 링버퍼에 넣는다.
//   3) 되감기 — 과거 시점의 위치로 히트박스만 옮긴다.
//      CharacterController와 transform은 건드리지 않으므로
//      이동 시뮬레이션과 재조정률 0%에 영향이 없다.
//   4) 비파괴 조회 — 아무것도 옮기지 않고 과거 위치만 계산해 돌려준다. (W7 Day 2)
//
//  왜 CharacterController를 안 쓰고 별도 콜라이더를 두는가
//   CharacterController를 움직이려면 enabled 토글이 필요해 지저분하고,
//   이동 물리에 부작용이 생긴다. 히트박스를 분리하면 서버가 자유롭게
//   과거로 옮겼다 되돌릴 수 있다.
//
//  사전 준비 (수동)
//   Project Settings > Tags and Layers 에 "Hitbox" 레이어 추가
//   Project Settings > Physics > Layer Collision Matrix 에서
//   Hitbox 행의 체크를 전부 해제 (다른 무엇과도 충돌하지 않게)
//   플레이어 루트는 "Player" 레이어에 둔다. Default 에 두면 현재 위치의
//   CharacterController 캡슐이 되감긴 히트박스보다 먼저 명중해
//   랙 보상이 무력화된다. (W7 Day 1 에서 실측으로 확인)
//
//  ─────────────────────────────────────────────────────────────────
//  W7 Day 2 변경 (2026-09-04)
//
//  (1) TrySampleAt() 추가 — 되감기 없이 과거 위치만 조회한다.
//      20Hz 가시성 루프는 (관찰자 × 대상) 쌍마다 상대의 과거 위치가
//      필요한데, 매번 transform 을 옮겼다 되돌리면 비용도 크고
//      되감기 상태가 중첩되어 꼬인다. 값만 계산해 돌려준다.
//
//  (2) 이력 범위 밖을 false 로 구분한다.  ★
//      기존 Sample() 은 세 갈래가 모두 true 였다. 요청 시각이 버퍼(2.1초)
//      보다 오래되면 가장 오래된 값으로 때우고 성공처럼 반환했다.
//      V-LOS-01 이 "이력이 안 닿는 순간"과 "정상 차폐"를 구분하지 못하면
//      렉 구간에서 정상 플레이어를 위반 처리한다.
//      → "판정할 수 없으면 무죄". 범위 밖이면 false 를 돌려
//        검증기가 그 프레임을 건너뛰게 한다.
//      RewindTo() 는 히트 판정 연속성을 위해 기존대로 폴백을 쓰되,
//      LastRewindInRange 로 근사였는지 알린다.
//
//  (3) 히트박스 치수를 public 으로 노출. 조준각 계산에 중심 좌표가 필요하다.
//
//  (4) _savedPos / _savedYaw 제거. Restore() 가 localPosition 초기화로
//      부모를 다시 따라가게 하므로 실제로 쓰이지 않던 필드였다.
// =====================================================================

using Unity.Netcode;
using UnityEngine;

public class PlayerRewind : NetworkBehaviour
{
    // --- 히트박스 치수 ---
    // CharacterController: Height=2, Radius=0.5, Center=(0,0,0)
    // 즉 캡슐이 transform.y -1.0 ~ +1.0 을 차지한다.
    private const float BodyHeight = 1.6f;
    private const float BodyRadius = 0.5f;
    private static readonly Vector3 BodyCenter = new Vector3(0f, -0.2f, 0f);   // -1.0 ~ +0.6

    private const float HeadRadius = 0.25f;
    private static readonly Vector3 HeadCenter = new Vector3(0f, 0.75f, 0f);   // +0.5 ~ +1.0

    // --- 치수 노출 (조준각 계산용) ---
    // 오프셋이 Y축 성분뿐이라 yaw 회전과 무관하다. 그래서 루트 위치에
    // 더하기만 하면 중심 좌표가 나온다.
    public static Vector3 BodyCenterOffset => BodyCenter;
    public static Vector3 HeadCenterOffset => HeadCenter;
    public static float BodyRadiusValue => BodyRadius;
    public static float HeadRadiusValue => HeadRadius;

    /// <summary>루트 위치로부터 몸통 중심 좌표를 구한다.</summary>
    public static Vector3 BodyCenterFrom(Vector3 rootPos) => rootPos + BodyCenter;

    /// <summary>루트 위치로부터 머리 중심 좌표를 구한다.</summary>
    public static Vector3 HeadCenterFrom(Vector3 rootPos) => rootPos + HeadCenter;

    // --- 이력 버퍼 ---
    // 60Hz * 128 = 약 2.1초. MaxRewindSec 0.5초를 충분히 덮는다.
    private const int HistorySize = 128;

    private struct Snapshot
    {
        public float time;      // Time.realtimeSinceStartup (서버 기준)
        public Vector3 position;
        public float yaw;
    }

    private readonly Snapshot[] _history = new Snapshot[HistorySize];
    private int _writeIndex = 0;
    private int _count = 0;

    // --- 히트박스 ---
    private Transform _hitboxRoot;
    private CapsuleCollider _bodyCollider;
    private SphereCollider _headCollider;

    public Collider BodyCollider => _bodyCollider;
    public Collider HeadCollider => _headCollider;

    private bool _rewound;

    /// <summary>되감기 중인가. 이력 기록을 멈춰야 하는지 판단한다.</summary>
    public bool IsRewound => _rewound;

    /// <summary>
    /// 마지막 RewindTo 가 이력 범위 안에서 보간됐는가.
    /// false 면 가장 오래된 스냅샷으로 근사한 것이라 정밀도를 신뢰할 수 없다.
    /// </summary>
    public bool LastRewindInRange { get; private set; } = true;

    // -----------------------------------------------------------------
    //  생성
    // -----------------------------------------------------------------

    public override void OnNetworkSpawn()
    {
        // 히트박스는 서버 판정 전용이다. 클라이언트에 만들 이유가 없고,
        // 만들면 로컬 물리 비용만 늘어난다.
        if (!IsServer) return;

        int layer = LayerMask.NameToLayer(WeaponConfig.HitboxLayerName);
        if (layer < 0)
        {
            Debug.LogError(
                $"[Rewind] '{WeaponConfig.HitboxLayerName}' 레이어가 없다. " +
                "Project Settings > Tags and Layers 에서 추가할 것.");
            enabled = false;
            return;
        }

        var root = new GameObject("Hitboxes");
        root.transform.SetParent(transform, false);
        root.layer = layer;
        _hitboxRoot = root.transform;

        var bodyGo = new GameObject("Body");
        bodyGo.transform.SetParent(_hitboxRoot, false);
        bodyGo.layer = layer;
        _bodyCollider = bodyGo.AddComponent<CapsuleCollider>();
        _bodyCollider.height = BodyHeight;
        _bodyCollider.radius = BodyRadius;
        _bodyCollider.center = BodyCenter;
        _bodyCollider.direction = 1;              // Y축
        _bodyCollider.isTrigger = false;

        var headGo = new GameObject("Head");
        headGo.transform.SetParent(_hitboxRoot, false);
        headGo.layer = layer;
        _headCollider = headGo.AddComponent<SphereCollider>();
        _headCollider.radius = HeadRadius;
        _headCollider.center = HeadCenter;
        _headCollider.isTrigger = false;

        Debug.Log($"[Rewind] 히트박스 생성 client={OwnerClientId} layer={layer}");
    }

    // -----------------------------------------------------------------
    //  이력 기록
    // -----------------------------------------------------------------

    private void FixedUpdate()
    {
        if (!IsServer) return;
        if (_rewound) return;      // 되감기 중에는 기록하지 않는다

        _history[_writeIndex] = new Snapshot
        {
            time = Time.realtimeSinceStartup,
            position = transform.position,
            yaw = transform.eulerAngles.y,
        };

        _writeIndex = (_writeIndex + 1) % HistorySize;
        if (_count < HistorySize) _count++;
    }

    // -----------------------------------------------------------------
    //  비파괴 조회 (W7 Day 2)
    // -----------------------------------------------------------------

    /// <summary>
    /// 아무것도 옮기지 않고 targetTime 시점의 루트 위치와 yaw 만 계산한다.
    ///
    /// 20Hz 가시성 루프가 쓴다. RewindTo/Restore 와 달리 상태를 바꾸지
    /// 않으므로 루프 안에서 자유롭게 호출할 수 있다.
    /// </summary>
    /// <returns>
    /// false = 이력이 targetTime 을 덮지 못한다. 검증을 보류할 것.
    ///         (out 값에는 가장 오래된 스냅샷이 들어가지만 신뢰하면 안 된다.)
    /// </returns>
    public bool TrySampleAt(float targetTime, out Vector3 rootPos, out float yaw)
    {
        if (!IsServer || _count < 2)
        {
            rootPos = transform.position;
            yaw = transform.eulerAngles.y;
            return false;
        }
        return Sample(targetTime, out rootPos, out yaw);
    }

    /// <summary>
    /// targetTime 시점의 몸통 중심 좌표. 조준각 계산의 기준점이다.
    /// </summary>
    public bool TryBodyCenterAt(float targetTime, out Vector3 center)
    {
        bool ok = TrySampleAt(targetTime, out Vector3 root, out _);
        center = BodyCenterFrom(root);
        return ok;
    }

    /// <summary>targetTime 시점의 머리 중심 좌표.</summary>
    public bool TryHeadCenterAt(float targetTime, out Vector3 center)
    {
        bool ok = TrySampleAt(targetTime, out Vector3 root, out _);
        center = HeadCenterFrom(root);
        return ok;
    }

    // -----------------------------------------------------------------
    //  되감기
    // -----------------------------------------------------------------

    /// <summary>
    /// 히트박스를 targetTime 시점의 위치로 옮긴다.
    /// 반드시 Restore()와 짝을 이루어 호출할 것.
    /// </summary>
    /// <returns>이력이 부족해 되감지 못했으면 false</returns>
    public bool RewindTo(float targetTime)
    {
        if (!IsServer || _rewound || _count < 2) return false;

        // 히트 판정은 연속성이 중요하므로 범위 밖이어도 근사값으로 진행한다.
        // 다만 근사였다는 사실은 LastRewindInRange 로 남긴다.
        LastRewindInRange = Sample(targetTime, out Vector3 pos, out float yaw);

        _rewound = true;
        _hitboxRoot.position = pos;
        _hitboxRoot.rotation = Quaternion.Euler(0f, yaw, 0f);
        return true;
    }

    /// <summary>되감기를 해제하고 히트박스를 현재 위치로 되돌린다.</summary>
    public void Restore()
    {
        if (!_rewound) return;
        _rewound = false;

        // 부모를 따라가도록 로컬 좌표를 초기화한다.
        _hitboxRoot.localPosition = Vector3.zero;
        _hitboxRoot.localRotation = Quaternion.identity;
    }

    /// <summary>
    /// 이력에서 targetTime 전후 두 스냅샷을 찾아 선형 보간한다.
    /// </summary>
    /// <returns>
    /// true  = 이력 범위 안에서 결정됐다 (보간 또는 "되감을 필요 없음").
    /// false = targetTime 이 버퍼보다 오래됐다. 가장 오래된 값으로 대체했다.
    /// </returns>
    private bool Sample(float targetTime, out Vector3 pos, out float yaw)
    {
        pos = transform.position;
        yaw = transform.eulerAngles.y;

        // 최신부터 거슬러 올라가며 targetTime을 감싸는 구간을 찾는다.
        int newest = (_writeIndex - 1 + HistorySize) % HistorySize;

        if (_history[newest].time <= targetTime)
        {
            // 요청 시각이 최신보다 미래다. 되감을 것이 없다.
            // 이력 부족이 아니라 정상 상황이므로 true.
            pos = _history[newest].position;
            yaw = _history[newest].yaw;
            return true;
        }

        for (int i = 1; i < _count; i++)
        {
            int cur = (newest - i + HistorySize) % HistorySize;
            int next = (cur + 1) % HistorySize;

            if (_history[cur].time <= targetTime)
            {
                float span = _history[next].time - _history[cur].time;
                float t = span > 1e-6f
                        ? Mathf.Clamp01((targetTime - _history[cur].time) / span)
                        : 0f;

                pos = Vector3.Lerp(_history[cur].position, _history[next].position, t);
                yaw = Mathf.LerpAngle(_history[cur].yaw, _history[next].yaw, t);
                return true;
            }
        }

        // 이력이 targetTime까지 닿지 않는다. 가장 오래된 값으로 대체하되
        // 호출자가 판정을 보류할 수 있도록 false 를 돌려준다.
        int oldest = _count < HistorySize ? 0 : _writeIndex;
        pos = _history[oldest].position;
        yaw = _history[oldest].yaw;
        return false;
    }

    /// <summary>디버그용. 현재 보관 중인 이력 길이(초).</summary>
    public float HistorySpanSec
    {
        get
        {
            if (_count < 2) return 0f;
            int newest = (_writeIndex - 1 + HistorySize) % HistorySize;
            int oldest = _count < HistorySize ? 0 : _writeIndex;
            return _history[newest].time - _history[oldest].time;
        }
    }

    public int HistoryCount => _count;

    /// <summary>레이캐스트 대상에서 잠시 제외할 때 쓴다(사수 자신).</summary>
    public void SetHitboxesActive(bool active)
    {
        if (_bodyCollider != null) _bodyCollider.enabled = active;
        if (_headCollider != null) _headCollider.enabled = active;
    }
}