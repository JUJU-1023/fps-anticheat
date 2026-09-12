// =====================================================================
//  MovementValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/MovementValidator.cs
//
//  V-MOVE-01 : 이동 입력 검증 (L2, 서버 권위)
//
//  설계 근거
//   이 프로젝트의 서버는 클라이언트의 "위치"를 받지 않고 "입력"만 받아
//   스스로 Simulate()한다. 따라서 서버가 만들어낸 위치 델타는 정의상
//   상한을 넘을 수 없고, 위치 기반 속도 검사는 절대 발화하지 않는다.
//
//   실제 스피드핵(Cheat Engine 등)은 클라이언트의 시간을 조작해
//   FixedUpdate를 더 자주 돌린다. 서버는 매번 정상 크기로 이동시키지만
//   호출 횟수가 늘어 캐릭터가 빨라진다.
//   → 이 구조에서 스피드핵의 관측 가능한 신호는 "입력 수신율"이다.
//
//  실측 근거 (2026-09-02, 정상 플레이 1482 샘플)
//   input_count 분포: 4(5) 5(38) 6(1394) 7(39) 8(4) 11(1) 12(1)
//   - 94%가 정확히 6, 지터로 ±2 흔들림, 평균은 6에 수렴
//   → 창 단위 카운팅이면 44건 오탐. 토큰 버킷이 이 편차를 흡수한다.
//
//  틱 비교에 관한 주의 (2026-09-02 수정)
//   클라이언트 틱과 서버 틱은 서로 다른 시점에 0에서 시작하는
//   독립된 시간축이다. 실측에서 server_tick=6171 일 때
//   client_tick=1930 으로 4241틱 차이가 났다.
//   따라서 "클라 틱이 서버 틱보다 앞선다"는 비교는 성립하지 않는다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W7 Day 5 실측으로 드러난 오탐과 수정 ★
//
//   S2(match 52, 정상 플레이 1039발)에서 183건이 발화했다.
//   치트는 쓰지 않았다. 로그를 보면 원인이 명확하다.
//
//     server_time              occurrences  rtt_ms
//     11:43:21.240                       1     232
//     11:43:32.223                      26     915
//     11:43:33.274                       5     772
//     11:43:35.457                       9    1106
//     11:43:39.408                      29     822
//     11:43:44.526                      65       6
//
//   RTT 가 평소 2~8ms 에서 1106ms 까지 튀었다. 네트워크가 끊겼다
//   복구되는 동안 밀린 입력이 한꺼번에 도착했고, 마지막 65건은
//   그 백로그가 몰려 들어온 순간이다.
//
//   (1) 충전율에 여유가 없었다
//
//       RefillPerSec 60 = 정상 입력율 60/s. 여유가 정확히 0이다.
//       잔량이 랜덤워크가 되어 지터만으로도 언젠가 반드시 0에 닿는다.
//       → 충전을 75/s 로 올려 25% 여유를 둔다.
//
//   (2) 용량이 히컵을 못 버텼다
//
//       용량 20 = 0.33초 분량. RTT 1초 스파이크의 백로그(60개)를
//       흡수할 수 없다.
//       → 90 (1.5초 분량) 으로 올린다.
//
//   (3) 순간 고갈과 지속 고갈을 구분하지 않았다  ★ 핵심
//
//       네트워크 히컵은 짧고, 스피드핵은 지속된다. 그것이 둘을
//       가르는 유일하고 확실한 차이다.
//       → 토큰이 SustainedDrainSec 이상 연속으로 0에 머물 때만
//         위반으로 기록한다. 그 전에는 차단만 하고 기록하지 않는다.
//
//   탐지 지연 트레이드오프
//     2배 스피드핵(120/s) 기준 용량 90 이 비는 데 2초, 지속 판정 1초를
//     더해 약 3초 뒤 탐지된다. 3배면 약 1.9초.
//     오탐을 줄이는 대신 탐지가 느려진다. 치트는 지속되므로 이 지연은
//     감수할 수 있다고 판단했다.
// =====================================================================

using UnityEngine;

public static class VMove
{
    // --- 토큰 버킷 ---
    /// <summary>
    /// 초당 충전 토큰 수.
    /// 정상 입력율(60/s)보다 25% 높게 둔다. 같으면 잔량이 랜덤워크가 되어
    /// 지터만으로도 언젠가 반드시 고갈된다. (W7 Day 5 실측)
    /// </summary>
    public const float RefillPerSec = 75f;

    /// <summary>
    /// 버킷 용량. 1.5초 분량.
    /// 네트워크 히컵으로 밀린 입력이 한꺼번에 도착하는 것을 흡수한다.
    /// 실측에서 RTT 1106ms 스파이크를 관측했으므로 1초 이상이 필요하다.
    /// </summary>
    public const float BucketCapacity = 90f;

    /// <summary>
    /// 토큰이 연속으로 이 시간 이상 0에 머물 때만 위반으로 기록한다.
    /// 네트워크 히컵은 짧고 스피드핵은 지속된다는 것이 판정 근거다.
    /// </summary>
    public const float SustainedDrainSec = 1.0f;

    // --- 틱 검사 ---
    /// <summary>
    /// 직전에 처리한 틱 대비 허용 점프 폭(2초분).
    /// 패킷 손실로 입력이 통째로 유실될 수 있으므로 여유가 필요하다.
    /// </summary>
    public const int MaxTickJump = 120;

    // --- 스폰 유예 ---
    /// <summary>스폰 직후 이 시간 동안은 검증하지 않는다.</summary>
    public const float SpawnGraceSec = 3f;

    // --- 위반 코드 ---
    public const string CODE = "V-MOVE-01";
}


/// <summary>
/// 위반 사유. detail_json에 그대로 실어 나중에 원인 분석에 쓴다.
/// </summary>
public enum MoveRejectReason
{
    None = 0,
    RateExceeded,    // 토큰 버킷 지속 고갈 = 스피드핵
    TickReplay,      // 이미 처리한 틱 재전송
    TickAhead,       // 직전 틱 대비 과도한 점프
    BadInput,        // NaN / 무한대 / move 크기 초과
}


/// <summary>
/// 플레이어 한 명분의 이동 입력 검증기. 서버에서만 인스턴스를 만든다.
/// 상태를 들고 있으므로 static이 아니다.
/// </summary>
public class MovementValidator
{
    // 토큰 버킷 상태
    private float _tokens = VMove.BucketCapacity;
    private float _lastRefillTime;

    /// <summary>
    /// 토큰이 0에 닿은 시각. 회복하면 -1로 되돌린다.
    /// 순간 고갈(네트워크 히컵)과 지속 고갈(치트)을 구분하는 기준이다.
    /// </summary>
    private float _drainStartTime = -1f;

    // 스폰 유예
    private float _spawnTime;

    // 통계 (디버그 / Grafana용)
    public int TotalChecked { get; private set; }
    public int TotalRejected { get; private set; }

    /// <summary>고갈됐지만 지속 기준 미달이라 기록하지 않은 횟수. 튜닝 확인용.</summary>
    public int TransientDrops { get; private set; }

    public MovementValidator(float now)
    {
        _spawnTime = now;
        _lastRefillTime = now;
    }

    /// <summary>서버 텔레포트/리스폰 후 호출. 유예를 다시 준다.</summary>
    public void ResetGrace(float now)
    {
        _spawnTime = now;
        _tokens = VMove.BucketCapacity;
        _lastRefillTime = now;
        _drainStartTime = -1f;
    }

    /// <summary>
    /// 입력 하나를 검증한다. None이면 시뮬레이션해도 좋다.
    ///
    /// 검사 순서
    ///  1) 값 위조   — 유예와 무관. 서버 안정성 문제라 항상 막는다.
    ///  2) 수신율    — 스피드핵의 고유 신호. 틱 검사보다 먼저 본다.
    ///  3) 틱 이상   — 리플레이 / 점프
    ///
    ///  2와 3의 순서가 중요하다. 틱 검사를 먼저 하면 스피드핵이
    ///  TickReplay로 분류되어 실제 원인이 가려진다.
    /// </summary>
    /// <param name="now">Time.realtimeSinceStartup (서버 실시간)</param>
    /// <param name="inputTick">클라이언트가 보낸 틱</param>
    /// <param name="lastProcessedTick">마지막으로 처리한 클라 틱 (-1이면 첫 입력)</param>
    /// <param name="move">입력 이동 벡터</param>
    /// <param name="yaw">입력 yaw</param>
    /// <param name="pitch">입력 pitch</param>
    public MoveRejectReason Validate(
        float now,
        int inputTick, int lastProcessedTick,
        Vector2 move, float yaw, float pitch)
    {
        TotalChecked++;

        // --- 1) 값 위조 검사 ---
        // NaN이 Quaternion.Euler를 거쳐 cc.Move()에 들어가면
        // transform이 영구히 오염되어 재조정으로도 복구되지 않는다.
        // 치트 대응 이전에 서버 안정성 문제이므로 유예 중에도 막는다.
        if (!IsFinite(move.x) || !IsFinite(move.y) ||
            !IsFinite(yaw) || !IsFinite(pitch))
        {
            TotalRejected++;
            return MoveRejectReason.BadInput;
        }

        // 정상 입력은 GetAxisRaw 조합이라 크기가 최대 sqrt(2).
        // 여유를 두고 1.5(제곱 2.25)를 상한으로 한다.
        if (move.sqrMagnitude > 2.25f)
        {
            TotalRejected++;
            return MoveRejectReason.BadInput;
        }

        // --- 토큰 충전 ---
        // 실시간 기준이라 서버 프레임률이 흔들려도 정확하다.
        float dt = now - _lastRefillTime;
        if (dt > 0f)
        {
            _tokens = Mathf.Min(VMove.BucketCapacity,
                                _tokens + dt * VMove.RefillPerSec);
            _lastRefillTime = now;
        }

        // --- 스폰 유예 ---
        // 접속 직후에는 입력이 몰려 도착한다(실측: client_tick 104에서 11개).
        // 이 구간은 토큰만 소비하고 판정하지 않는다.
        bool inGrace = (now - _spawnTime) < VMove.SpawnGraceSec;

        // --- 2) 수신율 검사 ---
        if (_tokens < 1f)
        {
            _tokens = 0f;

            // 고갈이 시작된 시각을 기록한다.
            if (_drainStartTime < 0f) _drainStartTime = now;

            bool sustained = (now - _drainStartTime) >= VMove.SustainedDrainSec;

            if (!inGrace && sustained)
            {
                TotalRejected++;
                return MoveRejectReason.RateExceeded;
            }

            // 순간 고갈. 차단은 하되 기록하지 않는다.
            // 네트워크 히컵으로 밀린 입력이 몰려 도착한 경우가 여기다.
            TransientDrops++;
            return MoveRejectReason.None;
        }

        _tokens -= 1f;
        _drainStartTime = -1f;      // 회복

        // --- 3) 틱 검사 ---
        // 같은 틱을 다시 보내면 서버가 cc.Move()를 두 번 적용해
        // 그만큼 빨라진다. 가장 싸게 막을 수 있는 구멍이다.
        if (inputTick <= lastProcessedTick)
        {
            if (!inGrace) { TotalRejected++; return MoveRejectReason.TickReplay; }
            return MoveRejectReason.None;
        }

        // 첫 입력(lastProcessedTick == -1)은 기준이 없으므로 건너뛴다.
        if (lastProcessedTick >= 0 &&
            inputTick > lastProcessedTick + VMove.MaxTickJump)
        {
            if (!inGrace) { TotalRejected++; return MoveRejectReason.TickAhead; }
            return MoveRejectReason.None;
        }

        return MoveRejectReason.None;
    }

    /// <summary>남은 토큰. 0에 가까울수록 수신율이 높다는 뜻이다.</summary>
    public float Tokens => _tokens;

    /// <summary>현재 연속 고갈 시간(초). 0이면 고갈 상태가 아니다.</summary>
    public float DrainSeconds(float now)
        => _drainStartTime < 0f ? 0f : now - _drainStartTime;

    private static bool IsFinite(float v)
        => !float.IsNaN(v) && !float.IsInfinity(v);
}