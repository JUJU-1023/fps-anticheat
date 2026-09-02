// =====================================================================
//  CheatHarness.cs
//  경로: game/Assets/Scripts/Testing/CheatHarness.cs
//
//  V-MOVE-01 검증용 치트 시뮬레이션. 소유 클라이언트에서만 동작한다.
//
//  왜 이렇게 만드는가
//   기존 F9 텔레포트는 클라이언트 로컬 transform만 움직인다.
//   서버는 입력만 받아 스스로 시뮬레이션하므로 아무 영향이 없고,
//   다음 틱 재조정에서 조용히 되돌아온다. V-MOVE는 발화하지 않는다.
//
//   실제 스피드핵(Cheat Engine 등)은 클라이언트의 시간을 조작해
//   FixedUpdate를 더 자주 돌린다. 서버 입장에서 관측되는 것은
//   "단위 시간당 입력 수신량 증가"다. 그래서 하네스도 같은 경로로,
//   즉 SubmitInputServerRpc를 더 많이 호출하는 방식으로 공격한다.
//
//   중요: 스피드핵은 시간이 빨리 흐르므로 틱 카운터도 함께 오른다.
//   같은 틱을 반복 전송하면 서버가 TickReplay로 분류해
//   스피드핵의 고유 신호(수신율 초과)가 가려진다.
//   따라서 NextFakeTick()으로 단조 증가하는 틱을 만들어 보낸다.
//
//  W11에서 BepInEx/Harmony 플러그인으로 대체될 예정인 개발용 도구다.
//
//  조작
//   F5 : 스피드핵 배율 순환 (1x -> 2x -> 3x -> 5x -> 1x)
//   F6 : 틱 리플레이 (같은 틱을 20회 재전송)
//   F7 : 틱 점프 (직전 틱보다 600 앞선 값 전송)
//   F8 : 입력 값 위조 (move 크기 10, yaw NaN)
//   F9 : 로컬 텔레포트 (기존 동작, 서버 미영향 — 대조군)
// =====================================================================

using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class CheatHarness : NetworkBehaviour
{
    [Tooltip("체크 해제 시 하네스 전체가 비활성. 데모/제출 빌드에서는 꺼둔다.")]
    [SerializeField] private bool enableHarness = true;

    /// <summary>입력 전송 배율. 1이면 정상.</summary>
    public int SpeedMultiplier { get; private set; } = 1;

    /// <summary>이번 틱에 요청된 일회성 공격.</summary>
    public enum OneShot { None, TickReplay, TickAhead, BadInput }
    public OneShot Pending { get; private set; } = OneShot.None;

    private static readonly int[] Multipliers = { 1, 2, 3, 5 };
    private int _mulIndex = 0;

    /// <summary>스피드핵용 가짜 틱 카운터. 실제 틱보다 배율만큼 빨리 흐른다.</summary>
    private int _fakeTick = -1;

    public bool Active => enableHarness && IsOwner;

    private void Update()
    {
        if (!Active) return;

        if (Input.GetKeyDown(KeyCode.F5))
        {
            _mulIndex = (_mulIndex + 1) % Multipliers.Length;
            SpeedMultiplier = Multipliers[_mulIndex];
            if (SpeedMultiplier == 1) _fakeTick = -1;   // 정상 복귀 시 리셋
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
    }

    /// <summary>
    /// 스피드핵용 다음 틱. 실제 틱보다 뒤처지지 않게 보정하면서
    /// 호출할 때마다 1씩 증가한다. 배율만큼 빨리 흐르는 시간을 흉내낸다.
    /// </summary>
    public int NextFakeTick(int realTick)
    {
        if (_fakeTick < realTick) _fakeTick = realTick;
        return _fakeTick++;
    }

    /// <summary>PlayerController가 소비한 뒤 호출한다.</summary>
    public void ConsumeOneShot() => Pending = OneShot.None;

    /// <summary>모든 치트를 끈다.</summary>
    public void ResetAll()
    {
        _mulIndex = 0;
        SpeedMultiplier = 1;
        Pending = OneShot.None;
        _fakeTick = -1;
    }
}
