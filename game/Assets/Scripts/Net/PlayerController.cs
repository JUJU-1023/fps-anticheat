// =====================================================================
//  PlayerController.cs
//  경로: game/Assets/Scripts/Net/PlayerController.cs
//
//  W7 Day 2 변경
//   (1) 서버 조준각 보관 (serverAimYaw / serverAimPitch)
//       Simulate() 는 transform.rotation 에 yaw 만 반영하고 pitch 는
//       StatePayload 로 나가고 끝이라 서버에 남지 않았다.
//       20Hz 가시성 루프가 조준 방향을 재구성하려면 둘 다 필요하다.
//   (2) VisibilitySystem 등록/해제
//   (3) V-MOVE 위반 기록에 서버 측정 RTT 를 넣는다 (기존 -1)
//
//  W7 Day 5 변경  ← 이번
//   (4) 연사핵 / 트리거봇 하네스 배선
//
//       두 치트 모두 기존 입력 경로로만 공격한다. 서버가 관측하는 것은
//       SubmitInputServerRpc 에 담긴 tick / yaw / pitch / buttons 뿐이라,
//       외부 프로세스가 메모리를 조작하든 여기서 값을 바꾸든
//       서버 측 흔적은 동일하다. L2 측정에는 이걸로 충분하다.
//
//       연사핵: 입력 개수는 60/s 그대로 두고 틱만 FireIntervalTicks 씩
//               부풀린다. V-MOVE 는 개수만 보므로 통과하고, 서버의 발사
//               게이트(틱 간격)도 매번 통과한다. 실시간 발사율만 6배가
//               되어 V-FIRE-01 의 토큰 버킷에만 걸린다.
//
//       트리거봇: 조준선에 적이 걸리면 그 프레임에 발사 비트를 켠다.
//               SPOT 직후 인간 하한 미만의 반응으로 관측된다.
// =====================================================================

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

    /// <summary>서버가 마지막으로 처리한 클라이언트 틱. 검증기가 기준으로 쓴다.</summary>
    public int LastProcessedTick => lastProcessedTick;

    // 서버에서 온 미처리 상태 (RPC 콜백에서 담고 FixedUpdate에서 소비)
    private StatePayload? pendingServerState = null;

    private RemotePlayerInterpolator interpolator;
    private PlayerTelemetry telemetry;

    /// <summary>V-MOVE-01 검증기. 서버에서만 생성된다.</summary>
    private MovementValidator validator;
    private CheatHarness cheat;
    private WeaponSystem weapon;
    private PlayerHealth health;

    // --- 시뮬레이션 상태 ---
    // 수직 속도는 위치와 별개로 유지되는 상태다. 재조정 시 위치만 되돌리고
    // 이 값을 복원하지 않으면 공중에서 예측이 발산한다.
    private float verticalVelocity = 0f;

    private float currentYaw = 0f;
    private float currentPitch = 0f;

    // -----------------------------------------------------------------
    //  서버 조준각 (W7 Day 2)
    // -----------------------------------------------------------------
    //
    //  마지막으로 "검증을 통과해 처리된" 입력의 각도만 보관한다.
    //  거부된 입력을 반영하면 치터가 위반을 감수하고 서버가 아는
    //  조준선을 흔들 수 있다.

    private float serverAimYaw = 0f;
    private float serverAimPitch = 0f;

    public float ServerAimYaw => serverAimYaw;
    public float ServerAimPitch => serverAimPitch;

    /// <summary>서버가 아는 조준 방향. FireHitscan 의 dir 계산과 동일하다.</summary>
    public Vector3 ServerAimDirection =>
        Quaternion.Euler(serverAimPitch, serverAimYaw, 0f) * Vector3.forward;

    /// <summary>서버 권위 눈 위치. 클라이언트 예측이 있으므로 되감지 않는다.</summary>
    public Vector3 ServerEyePosition => transform.position + WeaponConfig.EyeOffset;

    // --- RTT 측정 (클라이언트 표시용) ---
    // 주의: 이 값은 안티치트 판정에 쓰지 않는다. 클라이언트가 측정한 값이므로
    //       치터가 부풀려 검증 관용 범위를 넓힐 수 있다.
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
        cheat = GetComponent<CheatHarness>();
        weapon = GetComponent<WeaponSystem>();
        health = GetComponent<PlayerHealth>();

        currentYaw = transform.eulerAngles.y;

        if (IsServer)
        {
            validator = new MovementValidator(Time.realtimeSinceStartup);

            serverAimYaw = transform.eulerAngles.y;
            serverAimPitch = 0f;

            VisibilitySystem.EnsureExists();
            VisibilitySystem.Instance.Register(this);
        }

        if (IsOwner)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && VisibilitySystem.Instance != null)
            VisibilitySystem.Instance.Unregister(this);
    }

    void FixedUpdate()
    {
        if (NetworkTickSystem.Instance == null) return;
        int tick = NetworkTickSystem.Instance.CurrentTick;

        if (IsOwner)
        {
            // 사망 중에는 입력을 만들지 않는다.
            if (health != null && health.IsDead)
            {
                pendingServerState = null;
                return;
            }

            // 1) 서버 상태가 도착해 있으면 먼저 재조정
            if (pendingServerState.HasValue)
            {
                Reconcile(pendingServerState.Value, tick);
                pendingServerState = null;
            }

            // 2) 치트 시뮬레이션 (테스트용 — 커밋 시 CheatHarness로 분리 예정)
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

            // --- 치트 하네스 (테스트 전용) ---
            // 실제 치트와 같은 경로로 공격한다: 입력을 조작해 보낸다.
            if (cheat != null && cheat.Active)
            {
                switch (cheat.Pending)
                {
                    case CheatHarness.OneShot.TickReplay:
                        for (int i = 0; i < 20; i++)
                            SubmitInputServerRpc(input);      // 같은 틱 반복
                        cheat.ConsumeOneShot();
                        return;

                    case CheatHarness.OneShot.TickAhead:
                        var ahead = input;
                        ahead.tick = tick + 600;              // 10초 앞
                        SubmitInputServerRpc(ahead);
                        cheat.ConsumeOneShot();
                        return;

                    case CheatHarness.OneShot.BadInput:
                        var bad = input;
                        bad.move = new Vector2(10f, 10f);
                        bad.yaw = float.NaN;
                        SubmitInputServerRpc(bad);
                        cheat.ConsumeOneShot();
                        return;
                }

                // --- 연사핵 ---
                // 입력 개수는 60/s 그대로다. 틱만 FireIntervalTicks 씩 부풀려
                // 서버의 발사 게이트(틱 간격)를 매번 통과시킨다.
                //   V-MOVE   : 개수 60/s, 틱 점프 6 -> 둘 다 정상. 통과.
                //   V-FIRE-01: 실시간으로는 초당 60발 요청 -> 토큰 버킷이 잡는다.
                // 이동 속도는 변하지 않는다. 순수 연사속도 조작이다.
                if (cheat.RapidFire)
                {
                    var rapid = input;
                    rapid.tick = cheat.NextRapidTick(tick, WeaponConfig.FireIntervalTicks);
                    SubmitInputServerRpc(rapid);
                    return;
                }

                // --- 스피드핵 ---
                // 같은 입력을 배율만큼 전송. 시간 조작으로 FixedUpdate 가
                // 더 자주 도는 것과 서버 관측 결과가 동일하다.
                int mul = cheat.SpeedMultiplier;
                if (mul > 1)
                {
                    for (int i = 0; i < mul; i++)
                    {
                        var fast = input;
                        fast.tick = cheat.NextFakeTick(tick);
                        SubmitInputServerRpc(fast);
                    }
                    return;
                }
            }

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

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            return;                    // 이 클릭은 발사로 치지 않는다
        }

        if (health != null && health.IsDead) return;   // 사망 카메라와 충돌 방지

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
        if (Input.GetMouseButton(0)) buttons |= BTN_FIRE;

        // --- 치트: 발사 비트 주입 (테스트 전용) ---
        // 카메라 transform 대신 currentYaw/Pitch 로 방향을 재구성한다.
        // 서버가 input.yaw/pitch 로 하는 계산과 정확히 같아야
        // 클라 판정과 서버 판정이 어긋나지 않는다.
        if (cheat != null && cheat.Active)
        {
            if (cheat.RapidFire) buttons |= BTN_FIRE;

            if (cheat.TriggerBot)
            {
                Vector3 eye = transform.position + WeaponConfig.EyeOffset;
                Vector3 aim = Quaternion.Euler(currentPitch, currentYaw, 0f) * Vector3.forward;
                if (cheat.TriggerBotWantsFire(eye, aim)) buttons |= BTN_FIRE;
            }
        }

        // 반동을 시야에 적용한다. 서버도 같은 패턴을 알고 있어
        // 조작 시 서버 계산과 어긋난다(W8 노리코일 탐지).
        if (weapon != null)
        {
            Vector2 recoil = weapon.ClientTryFire(tick, (buttons & BTN_FIRE) != 0);
            if (recoil != Vector2.zero)
            {
                currentYaw = Mathf.Repeat(currentYaw + recoil.x, 360f);
                currentPitch = Mathf.Clamp(currentPitch - recoil.y, -89f, 89f);
                if (cam) cam.transform.localRotation = Quaternion.Euler(currentPitch, 0f, 0f);
            }
        }

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
        int serverTick = NetworkTickSystem.Instance != null
                       ? NetworkTickSystem.Instance.CurrentTick
                       : input.tick;

        int rttMs = ServerRttMs();

        // =============================================================
        //  ★ V-MOVE-01 (W6-D3) ★
        //
        //  위반이면 Simulate()를 아예 호출하지 않는다.
        //  롤백이 아니라 "서버가 움직여주지 않는다"가 처벌이다.
        //  클라이언트는 예측이 어긋나 다음 재조정에서 되돌아온다.
        // =============================================================
        if (validator != null)
        {
            var reason = validator.Validate(
                Time.realtimeSinceStartup,
                input.tick, lastProcessedTick,
                input.move, input.yaw, input.pitch);

            if (reason != MoveRejectReason.None)
            {
                ViolationLogger.Report(
                    clientId: OwnerClientId,
                    playerUid: telemetry != null ? telemetry.PlayerUid : "unknown",
                    code: VMove.CODE,
                    tick: serverTick,
                    severity: 2,
                    detail: reason.ToString(),
                    rttMs: rttMs);
                return;
            }
        }

        StatePayload authoritative = Simulate(input);
        lastProcessedTick = input.tick;

        // ★ W7-D2 ★ 검증을 통과한 입력의 조준각만 서버 상태로 남긴다.
        serverAimYaw = input.yaw;
        serverAimPitch = input.pitch;

        // ★ W6-D2 텔레메트리 ★
        if (telemetry != null)
        {
            telemetry.OnServerInput(
                serverTick: serverTick,
                clientTick: input.tick,
                pos: authoritative.position,
                vel: authoritative.velocity,
                yaw: authoritative.yaw,
                pitch: authoritative.pitch,
                buttons: input.buttons,
                grounded: IsGrounded());
        }

        BroadcastStateClientRpc(authoritative);

        // ★ W6.5 사격 ★
        weapon?.ServerProcessInput(input, serverTick, rttMs);
    }

    /// <summary>서버 측정 RTT. 클라이언트 보고값을 쓰지 않는다.</summary>
    public int ServerRttMs()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig?.NetworkTransport == null) return -1;
        if (nm.IsHost && OwnerClientId == nm.LocalClientId) return 0;
        try { return (int)nm.NetworkConfig.NetworkTransport.GetCurrentRtt(OwnerClientId); }
        catch { return -1; }
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

        // 텔레포트 직후에는 입력이 몰려 올 수 있으므로 유예를 다시 준다.
        validator?.ResetGrace(Time.realtimeSinceStartup);
        weapon?.ServerOnRespawn();

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