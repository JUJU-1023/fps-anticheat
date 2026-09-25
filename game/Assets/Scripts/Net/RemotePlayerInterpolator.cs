// =====================================================================
//  RemotePlayerInterpolator.cs
//  경로: game/Assets/Scripts/Net/RemotePlayerInterpolator.cs
//
//  원격(비-소유) 플레이어의 Transform 을 서버가 보내주는 StatePayload
//  스냅샷 사이에서 보간한다. 최신 수신 틱보다 약 100ms 뒤를 렌더링해서
//  네트워크 지터/패킷 순서 뒤바뀜으로 인한 끊김·순간이동을 숨긴다.
//
//  로컬 소유 플레이어(IsOwner)에서는 아무 동작도 하지 않는다.
//  그쪽은 PlayerController 의 예측(Simulate) + Reconcile() 이 담당한다.
//
//  interpolationDelayMs 는 WeaponConfig.InterpolationDelaySec 와 반드시
//  같아야 한다. 서버의 되감기 시간이 RTT/2 + 이 값으로 계산되므로,
//  어긋나면 랙 보상이 화면과 다른 시점을 되감는다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 이 파일을 읽기 전에 : 틱에는 축이 두 개 있다 ★
//
//   BroadcastStateClientRpc 가 보내는 StatePayload.tick
//     = input.tick = 그 플레이어를 소유한 클라이언트의 로컬 틱
//
//   ForceStateClientRpc(ServerTeleport) 가 보내는 StatePayload.tick
//     = NetworkTickSystem.CurrentTick = 서버 틱
//
//   두 값은 같은 축이 아니다. 실측 차이가 7,870 틱이었다(약 2분).
//   서로 다른 축의 틱을 같은 변수(latestTick)에 넣으면 조용히 깨진다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8.5 : 늦게 접속한 플레이어가 스폰 자리에 멈추는 버그 ★
//
//   증상 — 두 명 중 나중에 들어온 사람이, 먼저 들어온 사람 화면에서
//          스폰 지점에 붙박인 채 움직이지 않는다. 서버 위치는 정상이라
//          그 사람이 실제로 이동한 자리를 쏘면 맞는다.
//
//   원인 — 두 가지가 겹쳤다.
//
//     (1) 위의 "틱 축 두 개" 문제. 접속 시점이 다르면 플레이어마다
//         로컬 틱의 원점이 다르다.
//
//     (2) NGO 가 (Clone) 오브젝트를 재사용하면 Awake 가 다시 불리지
//         않아 latestTick 과 stateBuffer 에 이전 세션 값이 남는다.
//
//   그래서 남아 있던 latestTick=10631 상태에서 새로 접속한 상대의
//   첫 스냅샷 tick=286 이 들어오면
//
//       if (state.tick > latestTick) latestTick = state.tick;
//
//   이 영원히 거짓이 되어 latestTick 이 10631 에 박힌다. Update() 는
//   존재하지 않는 10625 틱을 계속 찾고, 보간은 완전히 멈춘다.
//
//   → ResetForSpawn() 으로 스폰마다 상태를 비운다.
//     PlayerController.OnNetworkSpawn 에서 호출한다.
//   → OnTeleport() 는 서버 틱을 latestTick 에 넣지 않는다. (아래 참조)
//   → 그래도 남는 경로를 위해 EnqueueState 에서 큰 폭의 틱 역행을
//     감지해 자체 복구한다.
//
//   ※ 캐릭터 모델이 원인이 아니다. 모델 로딩으로 두 클라이언트의 접속
//     시점 차이가 벌어지면서 기존 버그가 드러났을 뿐이다. 캡슐이던
//     시절에는 틱 차이가 작아 몇 초 버벅이다 저절로 복구됐다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8.5 : 리스폰마다 틱 역행 로그가 찍히던 이유 ★
//
//   OnTeleport 가 받는 state.tick 은 서버 틱인데, 그것을 latestTick 에
//   그대로 넣고 있었다. 그러면 바로 다음 스냅샷(클라 틱)이 항상
//   "역행"으로 보인다. 역행 폭이 매번 정확히 7,870 으로 같았던 것이
//   단서였다. 지터라면 값이 흔들렸을 것이다.
//
//   누군가 죽고 부활할 때마다 재현되므로 로그가 주기적으로 찍혔다.
//   동작 자체는 방어망이 복구해 정상이었지만, 복구 전 몇 프레임 동안
//   상대가 멈춰 보인다.
//
//   → OnTeleport 는 위치만 즉시 적용하고, 틱 기준은 세우지 않는다.
//     버퍼를 비워 두면 다음 스냅샷 두 개가 클라 틱 축으로 새 기준을
//     만든다. 33ms 정도라 눈에 띄지 않는다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 4 : pitch 를 루트에 적용하면 안 된다 ★
//
//   기존에는 yaw 만 적용했다. 캐릭터 모델이 붙으면 원격 플레이어가
//   위아래를 봐도 고개가 안 움직이므로 고쳐야 하는 것은 맞다.
//
//   다만 루트 Transform 에 pitch 를 넣으면 안 된다.
//
//     서버 Simulate()  transform.rotation = Euler(0, input.yaw, 0)
//     히트박스          루트의 자식이라 루트를 따라 돈다
//
//   루트를 기울이면 히트박스 캡슐이 통째로 기울어져 서버가 되감는
//   기하와 클라이언트 화면이 어긋난다. 랙 보상이 깨진다.
//   (W7 Day 1 의 레이어 마스크 문제와 같은 종류의 사고다.)
//
//   그래서 pitch 는 별도 피벗에만 적용한다.
//
//     PlayerCharacter (root)   ← yaw 만. 히트박스가 여기 달린다.
//       └ HeadPivot            ← pitch 만. 모델의 머리/상체.
//
//   ※ Animator 가 붙은 모델의 본을 pitchPivot 으로 쓰면 이 코드만으로는
//     동작하지 않는다. Animator 가 Update 이후에 본 포즈를 덮어쓰기
//     때문이다. W8.5 시점에는 pitchPivot 을 비워 둔다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 4 : 리스폰 시 끌려가는 문제 ★
//
//   PlayerController.ForceStateClientRpc 는 원격 플레이어의 위치를
//   직접 대입한다. 그런데 보간기는 그 사실을 모르므로, 다음 Update 에서
//   죽기 전 스냅샷으로 다시 끌어당긴다.
//
//   결과적으로 상대가 부활하면 스폰 지점에 나타났다가 시체 자리로
//   100ms 가량 끌려간다. 캐릭터 모델을 붙이면 바로 보인다.
//
//   → OnTeleport() 로 버퍼를 비우고 위치를 새로 잡는다.
//     PlayerController.ForceStateClientRpc 에서 호출해야 한다.
// =====================================================================

using Unity.Netcode;
using UnityEngine;

public class RemotePlayerInterpolator : MonoBehaviour
{
    [Header("Interpolation Settings")]
    [Tooltip("최신 수신 틱보다 몇 ms 뒤를 렌더링할지 (버퍼 지연). " +
             "WeaponConfig.InterpolationDelaySec 와 같아야 한다.")]
    [SerializeField] private float interpolationDelayMs = 100f;

    [Header("Pitch")]
    [Tooltip("pitch 를 적용할 피벗. 캐릭터 모델의 머리/상체 본을 넣는다. " +
             "★ 루트를 넣으면 히트박스가 기울어져 랙 보상이 깨진다. ★ " +
             "Animator 가 본을 덮어쓰므로 W8.5 시점에는 비워 둔다.")]
    [SerializeField] private Transform pitchPivot;

    [Header("Packet loss")]
    [Tooltip("보간할 스냅샷 쌍을 이 시간 동안 못 찾으면 최신 상태로 붙인다(초).")]
    [SerializeField] private float snapAfterSec = 0.25f;

    [Header("Session guard")]
    [Tooltip("틱이 이 값 이상 역행하면 새 세션으로 보고 버퍼를 초기화한다. " +
             "정상 지터로는 나올 수 없는 폭이어야 한다 (600틱 = 10초).")]
    [SerializeField] private int tickRegressionThreshold = 600;

    [Tooltip("틱 역행이 감지될 때 로그를 남길지. 정상 복구 경로이므로 " +
             "평소에는 꺼도 되지만, 증상 추적의 첫 단서라 기본은 켜 둔다.")]
    [SerializeField] private bool logTickRegression = true;

    // PlayerController와 동일한 CircularBuffer<T>를 그대로 재사용.
    // tick % size 인덱싱이므로, 슬롯에 든 값이 "진짜 그 tick의 값"인지는
    // 항상 value.tick == 원하는 tick 으로 검증해야 한다.
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    private int latestTick = -1;
    private int receivedCount = 0;
    private const int MIN_SNAPSHOTS_TO_INTERPOLATE = 2;

    /// <summary>보간할 쌍을 못 찾기 시작한 시각. 음수면 정상이다.</summary>
    private float _stalledSince = -1f;

    private NetworkObject netObj;

    /// <summary>
    /// 보간된 조준 pitch(도). 캐릭터 모델이 없어도 값은 갱신된다.
    /// 모델을 붙이면 pitchPivot 에 자동으로 적용되고, 다른 표현
    /// (예: 이름표 방향)에 쓰고 싶으면 이 값을 읽으면 된다.
    /// </summary>
    public float CurrentPitch { get; private set; }

    /// <summary>보간된 yaw(도).</summary>
    public float CurrentYaw { get; private set; }

    private void Awake()
    {
        netObj = GetComponent<NetworkObject>();

        if (pitchPivot == transform)
        {
            Debug.LogError(
                $"[INTERP] {name}: pitchPivot 에 루트를 지정했습니다. " +
                "루트가 기울면 히트박스도 함께 기울어 랙 보상이 깨집니다. " +
                "머리/상체 자식 오브젝트를 지정하세요.");
            pitchPivot = null;
        }
    }

    /// <summary>
    /// 스폰 시 보간 상태를 전부 비운다.
    ///
    /// ★ PlayerController.OnNetworkSpawn 에서 반드시 호출해야 한다. ★
    ///
    /// NGO 가 (Clone) 오브젝트를 재사용하면 Awake 가 다시 불리지 않아
    /// latestTick 과 stateBuffer 에 이전 세션 값이 남는다. 새로 접속한
    /// 플레이어의 로컬 틱은 0 부터 시작하므로, 남아 있던 큰 latestTick 을
    /// 영원히 넘지 못해 보간이 완전히 멈춘다.
    ///
    /// netObj 재취득도 여기서 한다. 재사용된 오브젝트는 Awake 를 거치지
    /// 않아 netObj 참조가 유효하지 않을 수 있고, null 이면 ShouldSkip 이
    /// 항상 true 가 되어 보간기가 통째로 죽는다.
    /// </summary>
    public void ResetForSpawn()
    {
        if (netObj == null) netObj = GetComponent<NetworkObject>();

        ClearBuffer();

        CurrentYaw = 0f;
        CurrentPitch = 0f;
    }

    /// <summary>
    /// 틱 기준을 포함해 버퍼 상태를 비운다.
    ///
    /// latestTick 을 -1 로 두면 다음에 들어오는 스냅샷이 새 기준이 된다.
    /// 어떤 틱 축에서 왔든 상관없이 복구되므로, 축을 알 수 없는 상황에서
    /// 가장 안전한 초기화다.
    /// </summary>
    private void ClearBuffer()
    {
        stateBuffer = new CircularBuffer<StatePayload>(1024);
        latestTick = -1;
        receivedCount = 0;
        _stalledSince = -1f;
    }

    /// <summary>
    /// PlayerController.BroadcastStateClientRpc 에서, 원격 플레이어인 경우
    /// transform.position 을 직접 대입하는 대신 이 메서드를 호출한다.
    ///
    /// 여기 들어오는 state.tick 은 항상 "소유 클라이언트의 로컬 틱"이다.
    /// 서버 틱을 넣으면 안 된다.
    /// </summary>
    public void EnqueueState(StatePayload state)
    {
        if (ShouldSkip()) return;  // 소유자는 절대 버퍼링하지 않음

        // --- 세션 전환 방어 ---
        //
        // 틱이 큰 폭으로 역행했다면 이 오브젝트가 다른 세션/다른 플레이어의
        // 것으로 재사용된 것이다. ResetForSpawn 이 어떤 경로로 누락되더라도
        // 여기서 스스로 복구한다. 이게 없으면 latestTick 이 옛 값에 박혀
        // 보간이 영구히 멈춘다.
        //
        // 임계값은 정상 지터로 도달할 수 없는 폭이어야 오탐이 없다.
        //
        // ※ 리스폰마다 이 로그가 찍힌다면 OnTeleport 가 서버 틱을
        //   latestTick 에 넣고 있다는 뜻이다. 파일 상단 참조.
        if (latestTick >= 0 && state.tick < latestTick - tickRegressionThreshold)
        {
            if (logTickRegression)
                Debug.Log(
                    $"[INTERP] {name}: 틱 역행 ({latestTick} → {state.tick}). " +
                    "오브젝트 재사용으로 보고 버퍼를 초기화합니다.");

            ClearBuffer();
        }

        stateBuffer.Set(state.tick, state);
        if (state.tick > latestTick) latestTick = state.tick;
        receivedCount++;
    }

    /// <summary>
    /// 서버가 위치를 강제로 옮겼을 때 호출한다 (스폰, 리스폰).
    ///
    /// 버퍼를 비우지 않으면 다음 Update 에서 옛 스냅샷으로 되돌아간다.
    /// 부활한 상대가 스폰 지점에 나타났다가 시체 자리로 끌려가는 원인이다.
    ///
    /// ★ state.tick 을 latestTick 에 넣지 않는다 ★
    ///
    /// 이 payload 의 tick 은 ServerTeleport 가 만든 "서버 틱"이고,
    /// EnqueueState 가 받는 tick 은 "소유 클라이언트의 로컬 틱"이다.
    /// 축이 다르므로 여기서 기준을 세우면 다음 스냅샷이 전부 역행으로
    /// 보인다(실측 7,870 틱 차이). 위치만 즉시 반영하고, 틱 기준은
    /// 이어서 도착하는 스냅샷 두 개가 새로 만들게 둔다. 33ms 정도라
    /// 화면에서 구분되지 않는다.
    ///
    /// PlayerController.ForceStateClientRpc 에서 원격 플레이어일 때
    /// 호출해야 한다.
    /// </summary>
    public void OnTeleport(StatePayload state)
    {
        if (ShouldSkip()) return;

        ClearBuffer();

        CurrentYaw = state.yaw;
        CurrentPitch = state.pitch;

        transform.position = state.position;
        transform.rotation = Quaternion.Euler(0f, state.yaw, 0f);
        ApplyPitch(state.pitch);
    }

    private void Update()
    {
        if (ShouldSkip()) return;
        if (receivedCount < MIN_SNAPSHOTS_TO_INTERPOLATE || latestTick < 0) return;
        if (NetworkTickSystem.TickInterval <= 0f) return;

        // "지금 렌더링해야 할 시점"을 틱 단위(소수)로 환산
        float delayTicks = (interpolationDelayMs / 1000f) / NetworkTickSystem.TickInterval;
        float targetTick = latestTick - delayTicks;

        int fromTick = Mathf.FloorToInt(targetTick);
        int toTick = fromTick + 1;

        StatePayload from = stateBuffer.Get(fromTick);
        StatePayload to = stateBuffer.Get(toTick);

        // 슬롯이 그 틱의 값이 아니면(아직 안 왔거나 덮어써짐) 보간하지 않고
        // 마지막으로 그려진 위치를 유지한다. 뚝뚝 끊기더라도 순간이동보다 낫다.
        //
        // 다만 무한히 버티면 패킷이 오래 끊겼을 때 상대가 허공에 멈춘 채
        // 남는다. snapAfterSec 이 지나면 최신 상태로 붙인다.
        if (from.tick != fromTick || to.tick != toTick)
        {
            HoldOrSnap();
            return;
        }

        _stalledSince = -1f;

        float t = Mathf.Clamp01(targetTick - fromTick);

        transform.position = Vector3.Lerp(from.position, to.position, t);

        CurrentYaw = Mathf.LerpAngle(from.yaw, to.yaw, t);
        CurrentPitch = Mathf.LerpAngle(from.pitch, to.pitch, t);

        // ★ 루트에는 yaw 만 넣는다. 히트박스가 루트를 따라 돌기 때문이다.
        transform.rotation = Quaternion.Euler(0f, CurrentYaw, 0f);
        ApplyPitch(CurrentPitch);
    }

    /// <summary>
    /// 보간 쌍을 못 찾는 동안의 처리.
    /// 잠깐은 버티고, 오래 끊기면 최신 상태로 붙인다.
    ///
    /// latestTick < 0 인 동안에는 Update 가 먼저 리턴하므로 여기 오지 않는다.
    /// (CircularBuffer 는 음수 인덱스를 방어하지 않는다.)
    /// </summary>
    private void HoldOrSnap()
    {
        if (_stalledSince < 0f)
        {
            _stalledSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _stalledSince < snapAfterSec) return;

        StatePayload latest = stateBuffer.Get(latestTick);
        if (latest.tick != latestTick) return;

        transform.position = latest.position;
        CurrentYaw = latest.yaw;
        CurrentPitch = latest.pitch;
        transform.rotation = Quaternion.Euler(0f, CurrentYaw, 0f);
        ApplyPitch(CurrentPitch);

        _stalledSince = -1f;
    }

    /// <summary>
    /// pitch 를 피벗에만 적용한다.
    ///
    /// 피벗이 없으면 아무것도 하지 않는다. CurrentPitch 값은 계속 갱신되므로
    /// 이름표 방향 등 다른 표현에서 읽어 쓸 수 있다.
    /// </summary>
    private void ApplyPitch(float pitch)
    {
        if (pitchPivot == null) return;
        pitchPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    /// <summary>
    /// 보간을 수행하면 안 되는 경우
    ///  - 내가 소유한 플레이어 (예측/재조정이 담당)
    ///  - 서버/호스트 (권위 있는 위치를 과거 값으로 덮어쓰면 안 됨)
    /// </summary>
    private bool ShouldSkip()
    {
        if (netObj == null) return true;
        if (netObj.IsOwner) return true;

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer) return true;

        return false;
    }
}