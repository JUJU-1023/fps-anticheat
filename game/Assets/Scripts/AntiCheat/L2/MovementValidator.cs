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
//   - 9 이상 2건은 모두 접속 직후(client_tick 104, 262)
//   → 창 단위 카운팅이면 44건 오탐. 토큰 버킷이 이 편차를 흡수한다.
//   → 버킷 용량 20이면 실측 최대 12를 여유 있게 수용한다.
//
//  틱 비교에 관한 주의 (2026-09-02 수정)
//   클라이언트 틱과 서버 틱은 서로 다른 시점에 0에서 시작하는
//   독립된 시간축이다. 실측에서 server_tick=6171 일 때
//   client_tick=1930 으로 4241틱 차이가 났다.
//   따라서 "클라 틱이 서버 틱보다 앞선다"는 비교는 성립하지 않는다.
//   대신 같은 시간축 안에서 lastProcessedTick 대비 점프 폭을 본다.
// =====================================================================

using UnityEngine;

public static class VMove
{
    // --- 토큰 버킷 ---
    /// <summary>초당 충전 토큰 수. 정상 FixedUpdate 주기와 같다.</summary>
    public const float RefillPerSec = 60f;

    /// <summary>
    /// 버킷 용량. 실측 최대 12에 여유를 둔 값.
    /// 클수록 오탐이 줄고 순간 폭주 허용량이 늘어난다.
    /// </summary>
    public const float BucketCapacity = 20f;

    // --- 틱 검사 ---
    /// <summary>
    /// 직전에 처리한 틱 대비 허용 점프 폭(2초분).
    /// 패킷 손실로 입력이 통째로 유실될 수 있으므로 여유가 필요하다.
    /// 서버 틱과 비교하지 않는 이유는 파일 상단 주석 참조.
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
    RateExceeded,    // 토큰 버킷 고갈 = 스피드핵
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

    // 스폰 유예
    private float _spawnTime;

    // 통계 (디버그 / Grafana용)
    public int TotalChecked { get; private set; }
    public int TotalRejected { get; private set; }

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
            if (!inGrace) { TotalRejected++; return MoveRejectReason.RateExceeded; }
            return MoveRejectReason.None;
        }
        _tokens -= 1f;

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

    private static bool IsFinite(float v)
        => !float.IsNaN(v) && !float.IsInfinity(v);
}