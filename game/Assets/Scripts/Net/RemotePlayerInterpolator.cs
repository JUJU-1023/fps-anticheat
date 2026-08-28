using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 원격(비-소유) 플레이어의 Transform을 서버가 보내주는 StatePayload 스냅샷
/// 사이에서 보간한다. 최신 수신 틱보다 약 100ms 뒤를 렌더링해서
/// 네트워크 지터/패킷 순서 뒤바뀜으로 인한 끊김/순간이동을 숨긴다.
///
/// 로컬 소유 플레이어(IsOwner)에서는 아무 동작도 하지 않는다 —
/// 그쪽은 PlayerController의 예측(Simulate) + Reconcile()이 담당한다.
/// </summary>
public class RemotePlayerInterpolator : MonoBehaviour
{
    [Header("Interpolation Settings")]
    [Tooltip("최신 수신 틱보다 몇 ms 뒤를 렌더링할지 (버퍼 지연)")]
    [SerializeField] private float interpolationDelayMs = 100f;

    // PlayerController와 동일한 CircularBuffer<T>를 그대로 재사용.
    // tick % size 인덱싱이므로, 슬롯에 든 값이 "진짜 그 tick의 값"인지는
    // 항상 value.tick == 원하는 tick 으로 검증해야 한다 (Reconcile()과 동일한 패턴).
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    private int latestTick = -1;
    private int receivedCount = 0;
    private const int MIN_SNAPSHOTS_TO_INTERPOLATE = 2;

    private NetworkObject netObj;

    private void Awake()
    {
        netObj = GetComponent<NetworkObject>();
    }

    /// <summary>
    /// PlayerController.BroadcastStateClientRpc에서, 원격 플레이어인 경우
    /// transform.position을 직접 대입하는 대신 이 메서드를 호출한다.
    /// </summary>
    public void EnqueueState(StatePayload state)
    {
        if (netObj != null && netObj.IsOwner) return; // 소유자는 절대 버퍼링하지 않음

        stateBuffer.Set(state.tick, state);
        if (state.tick > latestTick) latestTick = state.tick;
        receivedCount++;
    }

    private void Update()
    {
        if (netObj != null && netObj.IsOwner) return;
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
        // 마지막으로 그려진 위치를 유지한다 (뚝뚝 끊기더라도 순간이동보다 낫다)
        if (from.tick != fromTick || to.tick != toTick)
            return;

        float t = Mathf.Clamp01(targetTick - fromTick);

        transform.position = Vector3.Lerp(from.position, to.position, t);

        float yaw = Mathf.LerpAngle(from.yaw, to.yaw, t);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }
}