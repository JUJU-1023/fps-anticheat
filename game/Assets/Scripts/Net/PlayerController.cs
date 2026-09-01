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
    private RemotePlayerInterpolator interpolator;

    private int lastProcessedTick = -1;

    // --- RTT 측정 ---
    private CircularBuffer<float> sendTimeBuffer = new(1024);
    private float currentRttMs = 0f;
    public float CurrentRttMs => currentRttMs;

    // --- 재조정 통계 ---
    private int reconcileCount = 0;
    private int totalReconcileChecks = 0;
    private float maxError = 0f;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 2f;
    private float currentYaw = 0f;
    // 서버에서 온 미처리 상태 (RPC 콜백에서 담고 FixedUpdate에서 소비)
    private StatePayload? pendingServerState = null;

    public override void OnNetworkSpawn()
    {
        bool mine = IsOwner;
        Debug.Log($"[SPAWN] OwnerClientId={OwnerClientId} IsOwner={mine} IsServer={IsServer} pos={transform.position}");
        if (cam) cam.gameObject.SetActive(mine);
        if (audioListener) audioListener.enabled = mine;

        interpolator = GetComponent<RemotePlayerInterpolator>();
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

            sendTimeBuffer.Set(tick, Time.realtimeSinceStartup);

            SubmitInputServerRpc(input);

            // 4) 5초마다 통계 출력
            if (tick % 300 == 0)
            {
                float rate = totalReconcileChecks > 0
                    ? (float)reconcileCount / totalReconcileChecks * 100f
                    : 0f;
                Debug.Log($"[STAT] RTT={currentRttMs:F1}ms 재조정률={rate:F1}% ({reconcileCount}/{totalReconcileChecks}) 최대오차={maxError:F3}m");

                reconcileCount = 0;
                totalReconcileChecks = 0;
                maxError = 0f;
            }
        }
    }

    private InputPayload GatherInput(int tick)
    {
        // yaw를 입력으로 직접 누적 (transform에서 읽지 않는다)
        currentYaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
        currentYaw = Mathf.Repeat(currentYaw, 360f);

        return new InputPayload
        {
            tick = tick,
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
            yaw = currentYaw,
            pitch = 0f,
            buttons = 0
        };
    }

    // 클라/서버 공용 이동 함수 — 반드시 결정론적이어야 함
    private StatePayload Simulate(InputPayload input)
    {
        // transform.right/forward 대신 input.yaw로 방향을 재구성한다.
        // 이렇게 해야 replay 시점의 회전 상태와 무관하게 항상 같은 결과가 나온다.
        Quaternion rot = Quaternion.Euler(0f, input.yaw, 0f);
        Vector3 right = rot * Vector3.right;
        Vector3 forward = rot * Vector3.forward;

        Vector3 dir = (right * input.move.x + forward * input.move.y).normalized;

        cc.Move(dir * moveSpeed * NetworkTickSystem.TickInterval);
        cc.Move(Physics.gravity * NetworkTickSystem.TickInterval);

        // 시뮬레이션 결과로 회전도 확정한다
        transform.rotation = rot;

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
            // --- RTT 계산 ---
            float sentAt = sendTimeBuffer.Get(state.tick);
            if (sentAt > 0f)
            {
                float sample = (Time.realtimeSinceStartup - sentAt) * 1000f;
                // 지수이동평균(EMA)으로 튀는 값을 완화한다
                currentRttMs = Mathf.Lerp(currentRttMs, sample, 0.1f);
            }

            // RPC 콜백에서 물리를 직접 건드리지 않고 다음 FixedUpdate로 넘긴다
            pendingServerState = state;
        }
        else
        {
            if (interpolator != null)
                interpolator.EnqueueState(state);
        }
    }

    /// <summary>서버가 권위적으로 위치를 이동시킨다 (스폰, 리스폰 등).</summary>
    public void ServerTeleport(Vector3 position)
    {
        if (!IsServer) return;

        cc.enabled = false;
        transform.position = position;
        cc.enabled = true;

        // 클라이언트에게 즉시 알린다.
        var state = new StatePayload
        {
            tick = NetworkTickSystem.Instance != null ? NetworkTickSystem.Instance.CurrentTick : 0,
            position = position,
            velocity = Vector3.zero,
            yaw = transform.eulerAngles.y,
            pitch = 0f
        };
        ForceStateClientRpc(state);
    }

    [ClientRpc]
    private void ForceStateClientRpc(StatePayload state)
    {
        cc.enabled = false;
        transform.position = state.position;
        cc.enabled = true;

        if (IsOwner)
        {
            // 예측 버퍼를 서버 상태로 리셋 — 이후 replay가 엉뚱한 위치에서 시작하지 않도록
            stateBuffer.Set(state.tick, state);
        }
    }
    private void Reconcile(StatePayload serverState, int currentTick)
    {
        StatePayload predicted = stateBuffer.Get(serverState.tick);

        Debug.Log($"[REC-IN] serverTick={serverState.tick} bufTick={predicted.tick} cur={currentTick}");

        // 버퍼에 해당 tick 기록이 없으면(오래된 패킷 등) 무시
        if (predicted.tick != serverState.tick) return;

        float error = Vector3.Distance(predicted.position, serverState.position);

        // 통계 집계 (임계값 미만이어도 오차 자체는 기록한다)
        totalReconcileChecks++;
        if (error > maxError) maxError = error;

        if (error < reconcileThreshold) return;

        reconcileCount++;   // 실제로 되감기가 발생한 횟수

        // --- 되감기 ---
        cc.enabled = false;
        transform.position = serverState.position;
        transform.rotation = Quaternion.Euler(0f, serverState.yaw, 0f);
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