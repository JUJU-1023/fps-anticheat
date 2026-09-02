using Unity.Netcode;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float sprintMultiplier = 1.6f;
    [SerializeField] private float crouchMultiplier = 0.5f;

    [Header("Jump / Gravity")]
    [Tooltip("점프 최고 높이(m). 초기 상승 속도는 이 값에서 역산한다.")]
    [SerializeField] private float jumpHeight = 1.2f;
    [Tooltip("중력 가속도(m/s^2). 양수로 입력한다.")]
    [SerializeField] private float gravity = 20f;
    [Tooltip("지면에 붙어 있을 때 유지할 하향 속도. 0이면 경사에서 붕 뜬다.")]
    [SerializeField] private float groundedStickVelocity = -2f;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 2f;

    [Header("References")]
    [SerializeField] private CharacterController cc;
    [SerializeField] private Camera cam;
    [SerializeField] private AudioListener audioListener;

    [Header("Reconciliation")]
    [SerializeField] private float reconcileThreshold = 0.05f;   // 5cm

    // --- 지면 판정 ---
    // 평평한 지형 1개 전제. 바닥 표면 y = -1, CharacterController Height=2/Center=(0,0,0)
    // 이므로 캡슐 중심(Transform.y)이 0일 때 지면에 닿는 것이 이론값이다.
    //
    // CharacterController.isGrounded는 Move() 호출 결과에 의존해 replay 시 값이
    // 달라질 수 있으므로 쓰지 않는다 (결정론 유지).
    private const float GROUND_Y = 0f;
    private const float GROUND_EPSILON = 0.05f;

    /// <summary>
    /// 실제로 캐릭터가 정지하는 높이.
    /// CharacterController는 skinWidth(기본 0.08)만큼 접촉면 위에 뜬 채로 멈춘다.
    /// 이 값을 계산에 넣지 않으면 캐릭터가 영원히 공중으로 판정되어
    /// verticalVelocity가 무한히 발산하고 점프가 작동하지 않는다.
    /// skinWidth는 프리팹 직렬화 값이라 클라/서버가 동일하므로 결정론에 안전하다.
    /// </summary>
    private float RestY => GROUND_Y + cc.skinWidth;

    // --- 입력 비트 마스크 (InputPayload.buttons) ---
    private const byte BTN_JUMP = 1 << 0;
    private const byte BTN_FIRE = 1 << 1;
    private const byte BTN_CROUCH = 1 << 2;
    private const byte BTN_SPRINT = 1 << 3;

    private CircularBuffer<InputPayload> inputBuffer = new(1024);
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    private int lastProcessedTick = -1;

    // 서버에서 온 미처리 상태 (RPC 콜백에서 담고 FixedUpdate에서 소비)
    private StatePayload? pendingServerState = null;

    private RemotePlayerInterpolator interpolator;
    private PlayerTelemetry telemetry;

    // --- 시뮬레이션 상태 ---
    // 수직 속도는 위치와 별개로 유지되는 상태다. 재조정 시 위치만 되돌리고
    // 이 값을 복원하지 않으면 공중에서 예측이 발산한다.
    private float verticalVelocity = 0f;

    private float currentYaw = 0f;
    private float currentPitch = 0f;

    // --- RTT 측정 (클라이언트 표시용) ---
    // 주의: 이 값은 안티치트 판정에 쓰지 않는다. 클라이언트가 측정한 값이므로
    //       치터가 부풀려 검증 관용 범위를 넓힐 수 있다.
    //       텔레메트리의 rtt_ms는 PlayerTelemetry가 서버에서 따로 측정한다.
    private CircularBuffer<float> sendTimeBuffer = new(1024);
    private float currentRttMs = 0f;
    public float CurrentRttMs => currentRttMs;

    // --- 재조정 통계 ---
    private int reconcileCount = 0;
    private int totalReconcileChecks = 0;
    private float maxError = 0f;

    public override void OnNetworkSpawn()
    {
        bool mine = IsOwner;
        Debug.Log($"[SPAWN] OwnerClientId={OwnerClientId} IsOwner={mine} IsServer={IsServer} pos={transform.position}");
        if (cam) cam.gameObject.SetActive(mine);
        if (audioListener) audioListener.enabled = mine;

        interpolator = GetComponent<RemotePlayerInterpolator>();
        telemetry = GetComponent<PlayerTelemetry>();

        currentYaw = transform.eulerAngles.y;
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

            // 2) 치트 시뮬레이션 (테스트용 — W6 치트 플러그인으로 대체 예정)
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

    void Update()
    {
        // 마우스 입력은 프레임 단위로 들어오므로 Update에서 누적한다.
        // FixedUpdate에서 GetAxisRaw를 읽으면 프레임률에 따라 입력이 유실된다.
        if (!IsOwner) return;

        currentYaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
        currentYaw = Mathf.Repeat(currentYaw, 360f);

        currentPitch -= Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
        currentPitch = Mathf.Clamp(currentPitch, -89f, 89f);

        if (cam) cam.transform.localRotation = Quaternion.Euler(currentPitch, 0f, 0f);
    }

    private InputPayload GatherInput(int tick)
    {
        byte buttons = 0;
        if (Input.GetKey(KeyCode.Space)) buttons |= BTN_JUMP;
        if (Input.GetKey(KeyCode.LeftControl)) buttons |= BTN_CROUCH;
        if (Input.GetKey(KeyCode.LeftShift)) buttons |= BTN_SPRINT;

        return new InputPayload
        {
            tick = tick,
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
            yaw = currentYaw,
            pitch = currentPitch,
            buttons = buttons
        };
    }

    /// <summary>
    /// 클라/서버 공용 이동 함수. 같은 InputPayload와 같은 시작 상태를 주면
    /// 반드시 같은 StatePayload가 나와야 한다 (결정론).
    /// transform.right/forward를 쓰지 않고 input.yaw로 방향을 재구성하는 이유가 그것이다.
    /// </summary>
    private StatePayload Simulate(InputPayload input)
    {
        float dt = NetworkTickSystem.TickInterval;

        // --- 수평 이동 ---
        Quaternion rot = Quaternion.Euler(0f, input.yaw, 0f);
        Vector3 right = rot * Vector3.right;
        Vector3 forward = rot * Vector3.forward;
        Vector3 dir = (right * input.move.x + forward * input.move.y).normalized;

        float speed = moveSpeed;
        bool crouching = (input.buttons & BTN_CROUCH) != 0;
        bool sprinting = (input.buttons & BTN_SPRINT) != 0;

        // 앉기가 달리기보다 우선한다 (동시 입력 시 규칙을 고정해야 서버/클라가 일치한다)
        if (crouching) speed *= crouchMultiplier;
        else if (sprinting) speed *= sprintMultiplier;

        // --- 수직 이동 ---
        bool grounded = IsGrounded();

        if (grounded && verticalVelocity <= 0f)
        {
            // 지면에 붙어 있는 동안은 약한 하향 속도를 유지한다.
            verticalVelocity = groundedStickVelocity;

            if ((input.buttons & BTN_JUMP) != 0)
            {
                // v = sqrt(2 * g * h)
                verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
            }
        }
        else
        {
            // 공중: 가속도를 속도에 누적한다.
            verticalVelocity -= gravity * dt;
        }

        Vector3 motion = dir * speed;
        motion.y = verticalVelocity;

        cc.Move(motion * dt);

        // 바닥을 뚫고 내려가지 않도록 보정 (평평한 지형 전제).
        // 복원 높이는 GROUND_Y가 아니라 RestY다. GROUND_Y로 되돌리면
        // CharacterController가 다음 틱에 skinWidth만큼 밀어올려 진동한다.
        if (transform.position.y < RestY)
        {
            Vector3 p = transform.position;
            p.y = RestY;
            cc.enabled = false;
            transform.position = p;
            cc.enabled = true;
            verticalVelocity = groundedStickVelocity;
        }

        transform.rotation = rot;

        Vector3 outVelocity = dir * speed;
        outVelocity.y = verticalVelocity;

        return new StatePayload
        {
            tick = input.tick,
            position = transform.position,
            velocity = outVelocity,
            yaw = input.yaw,
            pitch = input.pitch
        };
    }

    private bool IsGrounded()
    {
        return transform.position.y <= RestY + GROUND_EPSILON;
    }

    [ServerRpc]
    private void SubmitInputServerRpc(InputPayload input)
    {
        // ★ V-MOVE 검증 지점 (W6 Day 3~4에서 여기에 삽입) ★
        //   - 입력 수신율 (토큰 버킷)  : 스피드핵
        //   - lastProcessedTick 역행   : 리플레이/중복 전송
        //   - NaN / 입력 크기 검사     : 값 위조

        StatePayload authoritative = Simulate(input);
        lastProcessedTick = input.tick;

        // ★ W6-D2 텔레메트리 ★
        if (telemetry != null)
        {
            telemetry.OnServerInput(
                serverTick: NetworkTickSystem.Instance != null
                            ? NetworkTickSystem.Instance.CurrentTick
                            : input.tick,
                clientTick: input.tick,
                pos: authoritative.position,
                vel: authoritative.velocity,
                yaw: authoritative.yaw,
                pitch: authoritative.pitch,
                buttons: input.buttons,
                grounded: IsGrounded());
        }

        BroadcastStateClientRpc(authoritative);
    }

    [ClientRpc]
    private void BroadcastStateClientRpc(StatePayload state)
    {
        if (IsOwner)
        {
            // --- RTT 계산 (표시용) ---
            float sentAt = sendTimeBuffer.Get(state.tick);
            if (sentAt > 0f)
            {
                float sample = (Time.realtimeSinceStartup - sentAt) * 1000f;
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
        verticalVelocity = 0f;

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
        verticalVelocity = state.velocity.y;

        if (IsOwner)
        {
            currentYaw = state.yaw;
            stateBuffer.Set(state.tick, state);
        }
    }

    private void Reconcile(StatePayload serverState, int currentTick)
    {
        StatePayload predicted = stateBuffer.Get(serverState.tick);

        // 버퍼에 해당 tick 기록이 없으면(오래된 패킷 등) 무시
        if (predicted.tick != serverState.tick) return;

        float error = Vector3.Distance(predicted.position, serverState.position);

        totalReconcileChecks++;
        if (error > maxError) maxError = error;

        if (error < reconcileThreshold) return;

        reconcileCount++;

        // --- 되감기 ---
        cc.enabled = false;
        transform.position = serverState.position;
        transform.rotation = Quaternion.Euler(0f, serverState.yaw, 0f);
        cc.enabled = true;

        // 수직 속도도 서버 값으로 되돌린다. 이걸 빼면 공중 재조정 후 낙하가 어긋난다.
        verticalVelocity = serverState.velocity.y;

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