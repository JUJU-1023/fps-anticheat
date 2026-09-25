// =====================================================================
//  CheatHarness.cs
//  경로: game/Assets/Scripts/Testing/CheatHarness.cs
//
//  L2 검증기 측정용 치트 시뮬레이션. 소유 클라이언트에서만 동작한다.
//  W11 에서 BepInEx/Harmony 플러그인으로 대체될 개발용 도구다.
//
//  왜 이렇게 만드는가
//   서버는 입력만 받아 스스로 시뮬레이션하므로, 클라이언트 로컬
//   transform 을 아무리 건드려도 서버에는 영향이 없다(F9 가 그 대조군이다).
//   실제 치트가 서버에 남기는 흔적은 "입력이 어떻게 오는가"뿐이다.
//   그래서 하네스도 전부 SubmitInputServerRpc 경로로 공격한다.
//
//  조작
//   F4  : 노리코일    ← W8 Day 2
//   F5  : 스피드핵 배율 순환 (1x -> 2x -> 3x -> 5x -> 1x)
//   F6  : 틱 리플레이 (같은 틱을 20회 재전송)
//   F7  : 틱 점프 (직전 틱보다 600 앞선 값 전송)
//   F8  : 입력 값 위조 (move 크기 10, yaw NaN)
//   F9  : 로컬 텔레포트 (서버 미영향 — 대조군)
//   F10 : 연사핵      ← W7 Day 5
//   F11 : 트리거봇    ← W7 Day 5
//
//  ─────────────────────────────────────────────────────────────────
//  F10 연사핵
//
//   서버의 발사 게이트는 input.tick 간격을 본다. 그런데 input.tick 은
//   클라이언트가 정하고, V-MOVE-01 은 입력 "개수"만 60/s 로 제한할 뿐
//   틱 "증가폭"은 MaxTickJump(120) 까지 허용한다.
//
//     입력 개수 60/s 유지          → V-MOVE 통과
//     매 입력마다 틱을 6씩 증가     → 발사 게이트도 매번 통과
//     결과: 초당 10발이 초당 60발
//
//   이동 속도는 변하지 않는다. 서버가 입력당 고정 dt 로 시뮬레이션하므로
//   V-MOVE 로는 관측되지 않는 순수 연사속도 조작이다.
//   V-FIRE-01 의 서버 실시간 토큰 버킷만 이걸 잡는다.
//
//   ※ 틱을 부풀리면 서버 응답 틱이 클라 stateBuffer 와 맞지 않아
//     재조정이 동작하지 않는다. 스피드핵 경로와 같은 한계이고
//     테스트 도구이므로 그대로 둔다.
//
//  ─────────────────────────────────────────────────────────────────
//  F11 트리거봇
//
//   클라이언트가 카메라에서 Player 레이어로 레이를 쏴 적이 걸리면
//   그 프레임에 발사 비트를 켠다. 실제 트리거봇이 하는 일과 같다.
//
//   서버 입장에서는 "SPOT 직후 인간 하한 미만의 반응"으로 관측된다.
//   다만 조준선이 이미 적을 향해 있어야 발화하므로, 측정하려면
//   코너를 겨누고 있다가 적이 나오게 해야 한다.
//   시야에 들어와도 조준선 밖이면 봇도 쏘지 않는다.
//
//   히트박스는 서버 전용이라 클라이언트에는 없다. 대신 플레이어 루트의
//   CharacterController 캡슐이 Player 레이어에 있어 그것을 맞힌다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ F4 노리코일 (W8 Day 2) ★
//
//   이 토글은 값을 아무것도 조작하지 않는다. PlayerController.GatherInput
//   에서 ClientTryFire 는 그대로 호출하고, 반환된 반동 벡터를 시야에
//   적용하는 단계만 건너뛴다. _clientShotIndex 증가와 OnClientFired
//   이벤트는 정상 진행된다.
//
//   이게 실제 핵과 서버 관측 결과가 같은 이유다. 외부 프로세스가 메모리에서
//   반동 적용을 무력화하든 마우스로 역보정하든, 서버가 보는 것은
//   input.pitch 하나뿐이다. 발사 타이밍과 shotIndex 는 정상 그대로이므로
//   V-FIRE-01 의 토큰 버킷에는 걸리지 않는다. 순수 조준각 조작이다.
//
//   서버 관측 (match 57 정상 플레이 실측 대비)
//
//     정상 : pitch 가 반동만큼 밀려 올라가고 플레이어가 손으로 되내린다.
//            잔차 = (pitch 변화) + (반동값)
//            평균 0.75~0.96, 표준편차 1.12~2.15 도
//     핵   : pitch 가 반동만큼 움직이지 않는다.
//            잔차 = 반동값 자체 → 표준편차 약 0.24 도
//            (램프 구간 0.4→1.1 의 변화폭만 남는다)
//
//   평균으로는 사람과 구분되지 않는다. 잘하는 플레이어도 반동을 거의
//   전부 상쇄하기 때문이다. 구분되는 것은 편차다. 사람은 손으로
//   따라가느라 흔들리고, 핵은 기계적으로 정확하다.
//
//   ※ 측정 시 마우스를 가만히 두지 말 것. 실제 핵 유저도 조준은 한다.
//     정지 상태로 재면 편차가 인위적으로 낮게 나와 임계가 왜곡된다.
// =====================================================================

using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class CheatHarness : NetworkBehaviour
{
    [Tooltip("체크 해제 시 하네스 전체가 비활성. 데모/제출 빌드에서는 꺼둔다.")]
    [SerializeField] private bool enableHarness = true;

    [Tooltip("트리거봇 탐지 사거리(m).")]
    [SerializeField] private float triggerBotRange = 200f;

    /// <summary>입력 전송 배율. 1이면 정상.</summary>
    public int SpeedMultiplier { get; private set; } = 1;

    /// <summary>연사핵. 틱을 부풀려 서버 발사 게이트를 매번 통과한다.</summary>
    public bool RapidFire { get; private set; }

    /// <summary>트리거봇. 조준선에 적이 있으면 자동 발사.</summary>
    public bool TriggerBot { get; private set; }

    /// <summary>
    /// 노리코일. 반동을 시야에 적용하지 않는다.
    ///
    /// PlayerController.GatherInput 이 이 값을 읽어 적용 단계만 건너뛴다.
    /// ClientTryFire 호출 자체는 유지되므로 _clientShotIndex 와
    /// OnClientFired 는 정상 진행된다. 발사 타이밍도 변하지 않는다.
    /// </summary>
    public bool NoRecoil { get; private set; }

    /// <summary>이번 틱에 요청된 일회성 공격.</summary>
    public enum OneShot { None, TickReplay, TickAhead, BadInput }
    public OneShot Pending { get; private set; } = OneShot.None;

    private static readonly int[] Multipliers = { 1, 2, 3, 5 };
    private int _mulIndex = 0;

    /// <summary>스피드핵용 가짜 틱 카운터.</summary>
    private int _fakeTick = -1;

    /// <summary>연사핵용 가짜 틱 카운터.</summary>
    private int _rapidTick = -1;

    /// <summary>트리거봇이 볼 레이어. 플레이어 본체만.</summary>
    private int _playerMask;

    public bool Active => enableHarness && IsOwner;

    public override void OnNetworkSpawn()
    {
        int player = LayerMask.NameToLayer("Player");
        _playerMask = player >= 0 ? (1 << player) : 0;

        if (player < 0 && IsOwner && enableHarness)
            Debug.LogWarning("[CHEAT] 'Player' 레이어가 없다. 트리거봇이 동작하지 않는다.");
    }

    private void Update()
    {
        if (!Active) return;

        // 노리코일 토글. 켠 시각을 로그에 남긴다.
        // 한 매치 안에서 정상/핵 구간을 나눠 측정할 때 경계를 찾는 근거가 된다.
        if (Input.GetKeyDown(KeyCode.F4))
        {
            NoRecoil = !NoRecoil;
            Debug.Log($"[CHEAT] 노리코일 = {NoRecoil} " +
                      $"({(NoRecoil ? "반동 시야 적용 차단" : "정상")}) " +
                      $"t={Time.realtimeSinceStartup:F3}");
        }

        if (Input.GetKeyDown(KeyCode.F5))
        {
            _mulIndex = (_mulIndex + 1) % Multipliers.Length;
            SpeedMultiplier = Multipliers[_mulIndex];
            if (SpeedMultiplier == 1) _fakeTick = -1;
            Debug.Log($"[CHEAT] 스피드핵 배율 = {SpeedMultiplier}x " +
                      $"(초당 입력 {60 * SpeedMultiplier}개)");
        }

        if (Input.GetKeyDown(KeyCode.F6))
        {
            Pending = OneShot.TickReplay;
            Debug.Log("[CHEAT] 틱 리플레이 요청");
        }

        if (Input.GetKeyDown(KeyCode.F7))
        {
            Pending = OneShot.TickAhead;
            Debug.Log("[CHEAT] 틱 점프 요청");
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            Pending = OneShot.BadInput;
            Debug.Log("[CHEAT] 입력 값 위조 요청");
        }

        if (Input.GetKeyDown(KeyCode.F10))
        {
            RapidFire = !RapidFire;
            if (!RapidFire) _rapidTick = -1;
            Debug.Log($"[CHEAT] 연사핵 = {RapidFire} " +
                      $"({(RapidFire ? "초당 60발 요청" : "정상")})");
        }

        if (Input.GetKeyDown(KeyCode.F11))
        {
            TriggerBot = !TriggerBot;
            Debug.Log($"[CHEAT] 트리거봇 = {TriggerBot}");
        }
    }

    /// <summary>
    /// 스피드핵용 다음 틱. 호출할 때마다 1씩 증가한다.
    /// 배율만큼 빨리 흐르는 시간을 흉내낸다.
    /// </summary>
    public int NextFakeTick(int realTick)
    {
        if (_fakeTick < realTick) _fakeTick = realTick;
        return _fakeTick++;
    }

    /// <summary>
    /// 연사핵용 다음 틱. stride 만큼씩 증가시켜 서버의 발사 간격 게이트를
    /// 매번 통과하게 만든다. stride 는 WeaponConfig.FireIntervalTicks 를 준다.
    /// </summary>
    public int NextRapidTick(int realTick, int stride)
    {
        if (_rapidTick < realTick) _rapidTick = realTick;
        int t = _rapidTick;
        _rapidTick += Mathf.Max(1, stride);
        return t;
    }

    /// <summary>
    /// 트리거봇 판정. 조준선에 적이 있으면 true.
    /// 자기 캡슐 안에서 출발하므로 자신은 맞지 않지만 한 번 더 확인한다.
    /// </summary>
    public bool TriggerBotWantsFire(Vector3 origin, Vector3 dir)
    {
        if (!TriggerBot || _playerMask == 0) return false;

        if (!Physics.Raycast(origin, dir, out RaycastHit hit,
                             triggerBotRange, _playerMask,
                             QueryTriggerInteraction.Ignore))
            return false;

        return hit.collider.transform.root != transform;
    }

    /// <summary>PlayerController가 소비한 뒤 호출한다.</summary>
    public void ConsumeOneShot() => Pending = OneShot.None;

    /// <summary>모든 치트를 끈다.</summary>
    public void ResetAll()
    {
        _mulIndex = 0;
        SpeedMultiplier = 1;
        RapidFire = false;
        TriggerBot = false;
        NoRecoil = false;
        Pending = OneShot.None;
        _fakeTick = -1;
        _rapidTick = -1;
    }
}