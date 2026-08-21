using Unity.Netcode;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private CharacterController cc;
    [SerializeField] private Camera cam;
    [SerializeField] private AudioListener audioListener;

    [Header("Reconciliation")]
    [SerializeField] private float reconcileThreshold = 0.05f;   // 5cm

    private CircularBuffer<InputPayload> inputBuffer = new(1024);
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    private int lastProcessedTick = -1;

    // 서버에서 온 미처리 상태 (RPC 콜백에서 담고 FixedUpdate에서 소비)
    private StatePayload? pendingServerState = null;

    public override void OnNetworkSpawn()
    {
        bool mine = IsOwner;
        Debug.Log($"[SPAWN] OwnerClientId={OwnerClientId} IsOwner={mine} IsServer={IsServer} pos={transform.position}");
        if (cam) cam.gameObject.SetActive(mine);
        if (audioListener) audioListener.enabled = mine;
    }

    void FixedUpdate()
    {
        if (NetworkTickSystem.Instance == null) return;
        int tick = NetworkTickSystem.Instance.CurrentTick;

        if (IsOwner)
        {
            // 1) 서버 상태가 도착해 있으면 먼저 재조정
            if (pendingServerState.HasValue)
            {
                Reconcile(pendingServerState.Value, tick);
                pendingServerState = null;
            }

            // 2) 치트 시뮬레이션 (테스트용 — W5 이후 제거)
            if (Input.GetKeyDown(KeyCode.F9))
            {
                cc.enabled = false;
                transform.position += transform.forward * 10f;
                cc.enabled = true;
                Debug.Log("[CHEAT] F9 teleport");
            }

            // 3) 이번 틱 입력 처리
            InputPayload input = GatherInput(tick);
            inputBuffer.Set(tick, input);

            StatePayload predicted = Simulate(input);
            stateBuffer.Set(tick, predicted);

            SubmitInputServerRpc(input);
        }
    }

    private InputPayload GatherInput(int tick)
    {
        return new InputPayload
        {
            tick = tick,
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
            yaw = transform.eulerAngles.y,
            pitch = 0f,
            buttons = 0
        };
    }

    // 클라/서버 공용 이동 함수 — 반드시 동일해야 함
    private StatePayload Simulate(InputPayload input)
    {
        Vector3 dir = (transform.right * input.move.x + transform.forward * input.move.y).normalized;
        cc.Move(dir * moveSpeed * NetworkTickSystem.TickInterval);
        cc.Move(Physics.gravity * NetworkTickSystem.TickInterval);

        return new StatePayload
        {
            tick = input.tick,
            position = transform.position,
            velocity = dir * moveSpeed,
            yaw = input.yaw,
            pitch = input.pitch
        };
    }

    [ServerRpc]
    private void SubmitInputServerRpc(InputPayload input)
    {
        // ★ V-MOVE 검증 지점 (W6~W7에서 여기에 속도/텔레포트 체크 추가) ★

        StatePayload authoritative = Simulate(input);
        lastProcessedTick = input.tick;

        BroadcastStateClientRpc(authoritative);
    }

    [ClientRpc]
    private void BroadcastStateClientRpc(StatePayload state)
    {
        if (IsOwner)
        {
            // RPC 콜백에서 물리를 직접 건드리지 않고 다음 FixedUpdate로 넘긴다
            pendingServerState = state;
        }
        else
        {
            transform.position = state.position;
        }
    }

    private void Reconcile(StatePayload serverState, int currentTick)
    {
        StatePayload predicted = stateBuffer.Get(serverState.tick);

        Debug.Log($"[REC-IN] serverTick={serverState.tick} bufTick={predicted.tick} cur={currentTick}");

        // 버퍼에 해당 tick 기록이 없으면(오래된 패킷 등) 무시
        if (predicted.tick != serverState.tick) return;

        float error = Vector3.Distance(predicted.position, serverState.position);
        if (error < reconcileThreshold) return;

        // --- 되감기 ---
        cc.enabled = false;
        transform.position = serverState.position;
        cc.enabled = true;

        stateBuffer.Set(serverState.tick, serverState);

        // --- 재생(replay): 서버가 아직 모르는 이후 입력들을 다시 적용 ---
        for (int t = serverState.tick + 1; t < currentTick; t++)
        {
            InputPayload input = inputBuffer.Get(t);
            if (input.tick != t) continue;

            StatePayload replayed = Simulate(input);
            stateBuffer.Set(t, replayed);
        }

        Debug.Log($"[RECONCILE] tick={serverState.tick} error={error:F3}m");
    }
}