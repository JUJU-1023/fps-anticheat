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
//  W8 Day 2 : RecoilRampShots 를 public 으로 노출 (V-RECOIL-01 이 램프
//             구간을 판정에서 제외하는 데 쓴다)
//  W8 Day 3 : 탄약 / 재장전 상수 추가  ← 이번 변경
// =====================================================================

using UnityEngine;

public static class WeaponConfig
{
    // --- 발사 ---
    /// <summary>연사 간격(틱). 6틱 = 0.1초 = 600 RPM.</summary>
    public const int FireIntervalTicks = 6;

    /// <summary>히트스캔 최대 사거리(m).</summary>
    public const float MaxRange = 100f;

    // --- 탄약 (W8 Day 3) ---
    /// <summary>탄창 한 개 분량.</summary>
    public const int MagSize = 30;

    /// <summary>
    /// 여분 탄약. 리스폰하면 이 값으로 복구된다.
    /// 데모 길이(매치 5분)에 비해 넉넉하므로 탄약 고갈로 경기가
    /// 멈추는 일은 없다. 측정 세션에서도 재장전 6회분이 확보된다.
    /// </summary>
    public const int ReserveAmmo = 150;

    /// <summary>
    /// 재장전 소요 시간(초). 서버 실시간 기준으로 잰다.
    /// 클라 틱으로 재면 틱을 부풀려 즉시 재장전할 수 있다.
    /// </summary>
    public const float ReloadSec = 2.0f;

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
    ///
    /// ※ 탄창 30발에서 램프 8발을 빼면 탄창당 표본이 22발이다.
    ///   V-RECOIL 의 창 40발은 두 탄창이면 찬다.
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