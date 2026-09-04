// =====================================================================
//  FireRateValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/FireRateValidator.cs
//
//  V-FIRE-01 : 발사 속도 검증 (L2, 서버 권위)
//
//  왜 서버 실시간인가
//   기존 검사는 input.tick 간격만 봤다. 그런데 input.tick 은 클라이언트가
//   정하는 값이고, V-MOVE-01 은 입력 "개수"를 60/s 로 제한할 뿐
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
//   버킷은 이 버스트를 용량으로 흡수하고 지속적인 초과만 남긴다.
//   V-MOVE-01 과 같은 방어 패턴이다.
//
//  역할 분담
//   틱 간격 검사 (WeaponSystem) : 게임플레이 게이트. 게임시간 기준이라 공정.
//   토큰 버킷   (이 파일)        : 실시간 상한. 위반 판정의 근거.
//
//  임계값 주의
//   BucketCapacity 4 는 근거 있는 추정치이지 실측값이 아니다.
//   W7 Day 5 정상 플레이 30분에서 오탐이 나오면 조정한다.
//   (V-MOVE 의 용량 20 은 실측 분포를 보고 정한 값이다.)
// =====================================================================

using UnityEngine;

public static class VFire
{
    /// <summary>서버 틱레이트. NetworkTickSystem 과 같아야 한다.</summary>
    public const float TickRate = 60f;

    /// <summary>
    /// 버킷 용량. 지터로 몰려 도착하는 입력 버스트를 흡수한다.
    /// 4 = 400ms 분량. 이 값만큼은 연사속도를 순간 초과할 수 있다.
    /// </summary>
    public const float BucketCapacity = 4f;

    /// <summary>스폰/리스폰 직후 유예. 이 구간은 차단하되 기록하지 않는다.</summary>
    public const float SpawnGraceSec = 2f;

    public const string CODE = "V-FIRE-01";
    public const int Severity = 2;
}


public enum FireRejectReason
{
    None = 0,
    RateExceeded,   // 실시간 발사율 초과 = 연사속도 조작
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

    public int TotalChecked  { get; private set; }
    public int TotalRejected { get; private set; }

    /// <param name="now">Time.realtimeSinceStartup</param>
    /// <param name="fireIntervalTicks">WeaponConfig.FireIntervalTicks</param>
    public FireRateValidator(float now, int fireIntervalTicks)
    {
        // 6틱 간격 → 초당 10발
        _refillPerSec   = VFire.TickRate / Mathf.Max(1, fireIntervalTicks);
        _tokens         = VFire.BucketCapacity;
        _lastRefillTime = now;
        _graceUntil     = now + VFire.SpawnGraceSec;
    }

    /// <summary>리스폰 후 호출. 버킷을 채우고 유예를 다시 준다.</summary>
    public void ResetGrace(float now)
    {
        _tokens         = VFire.BucketCapacity;
        _lastRefillTime = now;
        _graceUntil     = now + VFire.SpawnGraceSec;
    }

    /// <summary>
    /// 발사 1회를 시도한다.
    /// </summary>
    /// <returns>
    /// true  = 발사 허용.
    /// false = 차단. 이때 reason 이 None 이 아니면 위반으로 기록한다.
    ///         (유예 중에는 차단하되 reason 을 None 으로 두어 기록하지 않는다.
    ///          접속 직후 밀려 도착한 입력을 위반으로 남기지 않기 위해서다.)
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

            if (now < _graceUntil)
            {
                reason = FireRejectReason.None;   // 차단하되 기록은 안 한다
                return false;
            }

            TotalRejected++;
            reason = FireRejectReason.RateExceeded;
            return false;
        }

        _tokens -= 1f;
        reason = FireRejectReason.None;
        return true;
    }

    /// <summary>남은 토큰. 0 에 가까울수록 발사율이 상한에 붙어 있다는 뜻이다.</summary>
    public float Tokens => _tokens;
}
