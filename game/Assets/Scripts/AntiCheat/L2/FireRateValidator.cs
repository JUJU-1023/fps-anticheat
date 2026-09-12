// =====================================================================
//  FireRateValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/FireRateValidator.cs
//
//  V-FIRE-01 : 발사 속도 검증 (L2, 서버 권위)
//
//  왜 서버 실시간인가
//   기존 검사는 input.tick 간격만 봤다. 그런데 input.tick 은 클라이언트가
//   정하는 값이고, V-MOVE-01 은 입력 "개수"를 제한할 뿐
//   틱 "증가폭"은 MaxTickJump(120) 까지 허용한다.
//
//     초당 60개 전송, 매번 틱을 8씩 증가
//       틱 점프 8  <  MaxTickJump 120      → V-MOVE 통과
//       발사 간격 8 >= FireIntervalTicks 6 → 매 입력마다 발사
//       결과: 초당 10발이 초당 60발
//
//   이동 속도는 변하지 않는다(서버가 입력당 고정 dt 로 시뮬레이션하므로).
//   V-MOVE 로는 관측되지 않는 순수 연사속도 조작이다.
//
//  왜 단순 간격 검사가 아니라 토큰 버킷인가
//   "now - lastFire < interval" 은 지터에 취약하다. 회선이 잠깐 끊겼다
//   복구되면 입력 20개가 한꺼번에 도착하는데, 이는 게임시간 333ms 분량이라
//   원래 3발이 나가야 정상이다. 실시간 간격 검사는 1발만 내보내고
//   나머지 2발을 위반으로 찍는다.
//
//  역할 분담
//   틱 간격 검사 (WeaponSystem) : 게임플레이 게이트. 게임시간 기준이라 공정.
//   토큰 버킷   (이 파일)        : 실시간 상한. 위반 판정의 근거.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W7 Day 5 실측으로 드러난 오탐과 수정 ★
//
//   S1(match 51, 정상 플레이 1994발)에서 169건이 발화했다.
//   치트는 쓰지 않았다.
//
//     first_tick  last_tick  occurrences
//          21896      21955           60
//          21956      22015           60
//          22016      22064           49
//
//   틱 폭 59에 60건 = 매 틱마다 1건이다. 60은 치트 배율이 아니라
//   서버 틱레이트였다. 연사핵이라면 틱을 6씩 부풀리므로 60건에
//   360틱 폭이 나와야 한다.
//
//   원인 두 가지.
//
//   (1) 충전율에 여유가 없었다
//
//       refill = TickRate/FireIntervalTicks = 10/s
//       게임플레이 게이트도 정확히 10/s 를 허용한다. 여유가 0이다.
//       잔량이 랜덤워크가 되어 지터만으로도 언젠가 반드시 고갈된다.
//       → 충전을 정상 발사율의 1.5배로 둔다.
//
//   (2) 거부가 연쇄 증폭을 일으켰다  ★ 이것이 169건의 직접 원인
//
//       WeaponSystem 은 발사가 성공했을 때만 _lastFireTick 을 갱신한다.
//       버킷이 비어 거부되면 _lastFireTick 이 그 시점에 멈춘다.
//
//         이후 모든 틱에서 input.tick - _lastFireTick 이 계속 커지므로
//         게임플레이 게이트가 전부 통과 → 매 틱 이 검증기 호출
//         → 소비 60/s, 충전 10/s → 발사 버튼을 뗄 때까지 회복 불가
//
//       실측에서 3초간 지속되다 멈춘 것이 이 때문이다(버튼을 뗌).
//       → WeaponSystem 에서 거부 시에도 _lastFireTick 을 전진시킨다.
//
//   (3) 순간 고갈과 지속 고갈을 구분하지 않았다
//
//       V-MOVE 와 같은 처방. 토큰이 SustainedDrainSec 이상 연속으로
//       0에 머물 때만 기록한다.
//
//   탐지 확인
//     연사핵 60/s vs 충전 15/s, 용량 12
//       → 12/(60-15) = 0.27초에 고갈, 지속 판정 0.5초를 더해 약 0.8초 뒤 탐지.
//     치트는 수 초간 지속되므로 충분하다.
// =====================================================================

using UnityEngine;

public static class VFire
{
    /// <summary>서버 틱레이트. NetworkTickSystem 과 같아야 한다.</summary>
    public const float TickRate = 60f;

    /// <summary>
    /// 충전율 배수. 정상 발사율(TickRate/FireIntervalTicks)에 이 값을 곱한다.
    /// 1.0 이면 여유가 0이라 지터만으로도 언젠가 고갈된다. (W7 Day 5 실측)
    /// </summary>
    public const float RefillHeadroom = 1.5f;

    /// <summary>
    /// 버킷 용량. 1.2초 분량.
    /// 지터로 몰려 도착하는 입력 버스트를 흡수한다.
    /// </summary>
    public const float BucketCapacity = 12f;

    /// <summary>
    /// 토큰이 연속으로 이 시간 이상 0에 머물 때만 위반으로 기록한다.
    /// 지터는 짧고 연사핵은 지속된다는 것이 판정 근거다.
    /// </summary>
    public const float SustainedDrainSec = 0.5f;

    /// <summary>스폰/리스폰 직후 유예. 이 구간은 차단하되 기록하지 않는다.</summary>
    public const float SpawnGraceSec = 2f;

    public const string CODE = "V-FIRE-01";
    public const int Severity = 2;
}


public enum FireRejectReason
{
    None = 0,
    RateExceeded,   // 실시간 발사율 지속 초과 = 연사속도 조작
}


/// <summary>
/// 플레이어 한 명분의 발사 속도 검증기. 서버에서만 인스턴스를 만든다.
/// </summary>
public class FireRateValidator
{
    private readonly float _refillPerSec;

    private float _tokens;
    private float _lastRefillTime;
    private float _graceUntil;

    /// <summary>
    /// 토큰이 0에 닿은 시각. 회복하면 -1로 되돌린다.
    /// 순간 고갈(지터)과 지속 고갈(연사핵)을 구분하는 기준이다.
    /// </summary>
    private float _drainStartTime = -1f;

    public int TotalChecked { get; private set; }
    public int TotalRejected { get; private set; }

    /// <summary>고갈됐지만 지속 기준 미달이라 기록하지 않은 횟수. 튜닝 확인용.</summary>
    public int TransientDrops { get; private set; }

    /// <param name="now">Time.realtimeSinceStartup</param>
    /// <param name="fireIntervalTicks">WeaponConfig.FireIntervalTicks</param>
    public FireRateValidator(float now, int fireIntervalTicks)
    {
        // 6틱 간격 → 정상 10발/s. 여기에 여유를 곱해 15/s 로 충전한다.
        _refillPerSec = VFire.TickRate / Mathf.Max(1, fireIntervalTicks)
                        * VFire.RefillHeadroom;
        _tokens = VFire.BucketCapacity;
        _lastRefillTime = now;
        _graceUntil = now + VFire.SpawnGraceSec;
    }

    /// <summary>리스폰 후 호출. 버킷을 채우고 유예를 다시 준다.</summary>
    public void ResetGrace(float now)
    {
        _tokens = VFire.BucketCapacity;
        _lastRefillTime = now;
        _graceUntil = now + VFire.SpawnGraceSec;
        _drainStartTime = -1f;
    }

    /// <summary>
    /// 발사 1회를 시도한다.
    /// </summary>
    /// <returns>
    /// true  = 발사 허용.
    /// false = 차단. 이때 reason 이 None 이 아니면 위반으로 기록한다.
    ///         (유예 중이거나 순간 고갈이면 차단하되 reason 을 None 으로 두어
    ///          기록하지 않는다.)
    /// </returns>
    public bool TryFire(float now, out FireRejectReason reason)
    {
        TotalChecked++;

        // 실시간 기준 충전. 서버 프레임률이 흔들려도 정확하다.
        float dt = now - _lastRefillTime;
        if (dt > 0f)
        {
            _tokens = Mathf.Min(VFire.BucketCapacity, _tokens + dt * _refillPerSec);
            _lastRefillTime = now;
        }

        if (_tokens < 1f)
        {
            _tokens = 0f;

            // 고갈이 시작된 시각을 기록한다.
            if (_drainStartTime < 0f) _drainStartTime = now;

            bool sustained = (now - _drainStartTime) >= VFire.SustainedDrainSec;

            if (now < _graceUntil || !sustained)
            {
                if (!sustained) TransientDrops++;
                reason = FireRejectReason.None;   // 차단하되 기록은 안 한다
                return false;
            }

            TotalRejected++;
            reason = FireRejectReason.RateExceeded;
            return false;
        }

        _tokens -= 1f;
        _drainStartTime = -1f;      // 회복
        reason = FireRejectReason.None;
        return true;
    }

    /// <summary>남은 토큰. 0 에 가까울수록 발사율이 상한에 붙어 있다는 뜻이다.</summary>
    public float Tokens => _tokens;

    /// <summary>현재 연속 고갈 시간(초). 0이면 고갈 상태가 아니다.</summary>
    public float DrainSeconds(float now)
        => _drainStartTime < 0f ? 0f : now - _drainStartTime;
}