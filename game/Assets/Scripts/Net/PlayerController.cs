using Unity.Netcode;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private CharacterController cc;

    private CircularBuffer<InputPayload> inputBuffer = new(1024);
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    void Update()
    {
        if (!IsOwner) return;
        if (NetworkTickSystem.Instance == null) return;

        // 다음 FixedUpdate에서 처리할 입력을 미리 모아둠
    }

    void FixedUpdate()
    {
        if (!IsOwner) return;
        if (NetworkTickSystem.Instance == null) return;

        int tick = NetworkTickSystem.Instance.CurrentTick;

        InputPayload input = GatherInput(tick);
        inputBuffer.Set(tick, input);

        // 로컬 예측: 서버와 동일한 이동 함수로 즉시 적용
        StatePayload predicted = Simulate(input);
        stateBuffer.Set(tick, predicted);

        SubmitInputServerRpc(input);
    }

    private InputPayload GatherInput(int tick)
    {
        return new InputPayload
        {
            tick = tick,
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")),
            yaw = transform.eulerAngles.y,
            pitch = 0f, // 카메라 pitch 있으면 연결
            buttons = 0
        };
    }

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
        // Day 2에서 서버 시뮬레이션 구현
    }
}