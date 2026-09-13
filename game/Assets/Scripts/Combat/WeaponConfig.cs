// =====================================================================
//  WeaponConfig.cs
//  경로: game/Assets/Scripts/Combat/WeaponConfig.cs
//
//  무기 상수와 반동 패턴. 클라이언트와 서버가 같은 값을 본다.
//
//  반동을 서버도 아는 이유
//   클라이언트만 반동을 계산하면 서버는 이미 조작된 yaw/pitch를 받는다.
//   노리코일 핵(반동 자동 상쇄)이 정상 입력과 구별되지 않는다.
//
//   서버가 패턴을 알고 있으면 "이 플레이어의 조준점이 반동을 받았는가"를
//   판단할 수 있다. 반동이 적용되는 클라이언트에서 조준점을 고정하려면
//   마우스를 반동과 정확히 같은 양만큼 반대로 움직여야 한다. 노리코일은
//   손을 떼기만 하면 된다. 이 차이가 W8 탐지 지표가 된다.
//   (V-RECOIL-01, RecoilValidator.cs 참조)
//
//   따라서 반동 함수는 반드시 결정론적이어야 한다.
//   Random을 쓰면 클라/서버 값이 갈려 이 비교 자체가 불가능해진다.
//
//  ─────────────────────────────────────────────────────────────────
//  W8 Day 2 변경
//   RecoilRampShots 를 public 으로 노출. V-RECOIL-01 이 램프 구간
//   (반동이 발마다 커지는 구간)을 판정에서 제외하는 데 쓴다.
// =====================================================================

using UnityEngine;

public static class WeaponConfig
{
    // --- 발사 ---
    /// <summary>연사 간격(틱). 6틱 = 0.1초 = 600 RPM.</summary>
    public const int FireIntervalTicks = 6;

    /// <summary>히트스캔 최대 사거리(m).</summary>
    public const float MaxRange = 100f;

    // --- 데미지 ---
    public const int BodyDamage = 25;
    public const int HeadDamage = 100;   // 즉사

    public const int MaxHealth = 100;
    public const float RespawnDelaySec = 3f;

    // --- 시야 ---
    /// <summary>
    /// 캐릭터 중심으로부터 눈까지의 오프셋.
    /// PlayerCamera 의 localPosition 과 반드시 같아야 한다.
    /// 서버가 조준 원점을 재구성할 때 쓰며, 클라이언트가 보낸 위치는
    /// 신뢰하지 않는다.
    /// </summary>
    public static readonly Vector3 EyeOffset = new Vector3(0f, 0.75f, 0f);

    // --- 반동 ---
    /// <summary>
    /// 반동이 최대에 도달하는 발수.
    ///
    /// ★ public 인 이유 (W8 Day 2) ★
    /// V-RECOIL-01 이 이 값 이후의 발만 판정한다. 램프 구간에서는
    /// 반동이 0.4 에서 1.1 로 발마다 커지므로 "조준점 고정"의 난이도가
    /// 균일하지 않고, 정상과 핵의 경계가 흐려진다.
    /// </summary>
    public const int RecoilRampShots = 8;

    private const float RecoilVerticalStart = 0.4f;   // deg
    private const float RecoilVerticalMax = 1.1f;   // deg
    private const float RecoilHorizontalMax = 0.35f;  // deg

    /// <summary>연사 중단 판정(틱). 이만큼 쉬면 반동 인덱스가 초기화된다.</summary>
    public const int RecoilResetTicks = 21;   // 약 0.35초

    /// <summary>
    /// n번째 연사(0-based)의 반동. x = 수평(deg), y = 수직(deg).
    /// 결정론적이어야 하므로 Random 을 쓰지 않는다.
    /// </summary>
    public static Vector2 GetRecoil(int shotIndex)
    {
        float ramp = Mathf.Clamp01((float)shotIndex / RecoilRampShots);

        // 수직: 초반에 급하게 올라갔다가 상한에서 유지
        float v = Mathf.Lerp(RecoilVerticalStart, RecoilVerticalMax, ramp);

        // 수평: 결정론적 지그재그. 5발째부터 진폭이 최대가 된다.
        float hRamp = Mathf.Clamp01((float)shotIndex / 5f);
        float h = Mathf.Sin(shotIndex * 1.7f) * RecoilHorizontalMax * hRamp;

        return new Vector2(h, v);
    }

    /// <summary>
    /// 0발부터 n-1발까지의 반동 누적.
    /// 서버가 "이 시점의 이론적 조준점"을 계산할 때 쓴다.
    /// 텔레메트리의 expected_recoil_pitch 가 이 값이다.
    ///
    /// ※ 이 값은 0~n-1 누적이고 input.pitch 에는 이미 GetRecoil(n) 이
    ///   반영돼 있다. 두 값을 그대로 차분하면 한 칸 어긋나므로,
    ///   분석 쿼리는 shot_index 에서 반동을 직접 유도해야 한다.
    /// </summary>
    public static Vector2 GetAccumulatedRecoil(int shotCount)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < shotCount; i++) sum += GetRecoil(i);
        return sum;
    }

    // --- Lag Compensation ---
    /// <summary>
    /// 클라이언트 보간 지연(초).
    /// RemotePlayerInterpolator.interpolationDelayMs(100) 와 같아야 한다.
    /// 되감기 시간 = RTT/2 + 이 값.
    /// </summary>
    public const float InterpolationDelaySec = 0.100f;

    /// <summary>
    /// 되감기 상한(초). 서버 측정 RTT를 쓰므로 조작 여지는 없지만
    /// 이상값이 들어와도 과거를 지나치게 파고들지 않도록 막는다.
    /// </summary>
    public const float MaxRewindSec = 0.5f;

    // --- 레이어 ---
    /// <summary>히트박스 전용 레이어 이름. 수동으로 생성해야 한다.</summary>
    public const string HitboxLayerName = "Hitbox";
}