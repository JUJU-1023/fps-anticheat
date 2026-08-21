using Unity.Netcode;
using UnityEngine;

public class PlayerController : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private CharacterController cc;
    [SerializeField] private Camera cam;              // ← 추가
    [SerializeField] private AudioListener audioListener;  // ← 추가

    private CircularBuffer<InputPayload> inputBuffer = new(1024);
    private CircularBuffer<StatePayload> stateBuffer = new(1024);

    // 서버가 마지막으로 처리한 입력의 tick (재전송/누락 감지용, Day 3에서 활용)
    private int lastProcessedTick = -1;


    public override void OnNetworkSpawn()
    {
        // 내 캐릭터의 카메라만 켠다. 남의 캐릭터 카메라는 꺼야 화면이 안 뺏긴다.
        bool mine = IsOwner;
        if (cam) cam.gameObject.SetActive(mine);
        if (audioListener) audioListener.enabled = mine;
    }
    void FixedUpdate()
    {
        if (NetworkTickSystem.Instance == null) return;
        int tick = NetworkTickSystem.Instance.CurrentTick;

        if (IsOwner)
        {
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
            // Day 3: 여기서 예측 오차 비교 + 재조정(reconciliation) 수행
            Reconcile(state);
        }
        else
        {
            // 다른 클라이언트가 보는 원격 플레이어 — 그냥 위치 반영
            transform.position = state.position;
        }
    }

    private void Reconcile(StatePayload serverState)
    {
        // Day 3에서 구현
    }
}