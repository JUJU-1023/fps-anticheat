// =====================================================================
//  RecoilValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/RecoilValidator.cs
//
//  V-RECOIL-01 : 노리코일 검증 (L2, 서버 권위)
//
//  무엇을 재는가
//   조준점이 두 발 사이에 전혀 움직이지 않은 상태가 "연속으로"
//   몇 발이나 이어지는가.
//
//     frozen  ⟺  |pitch[n] - pitch[n-1]| < FreezeEpsilonDeg
//
//   반동은 클라이언트에서 InputPayload 생성 직전에 시야에 적용된다
//   (PlayerController.GatherInput). 따라서 frozen 이 뜻하는 바는
//   두 진영에서 완전히 다르다.
//
//     정상      반동이 조준점을 밀어 올린다. 고정하려면 마우스를
//               반동과 정확히 같은 양만큼 반대로 움직여야 한다.
//     노리코일  밀어 올릴 반동이 없다. 손을 떼면 그대로 고정된다.
//
//  ─────────────────────────────────────────────────────────────────
//  실측 (W8.5 확정)
//
//     clean   match 63, 64, 71, 99(2명), 101      max_run  2 ~ 4
//     cheat   match 72                            max_run  45
//     cheat   match 76                            max_run  57
//     cheat   match 65                            max_run  290
//
//   정상 상한 4 / 핵 하한 45. 11배.
//   임계 25 는 정상 쪽 6배, 핵 쪽 1.8배 여유다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 왜 비율이 아니라 연속 길이인가 ★
//
//   pitch 는 0.05도 격자 위에만 존재한다. 마우스 1카운트가 0.05도이고,
//   반동 1.1도는 정확히 22카운트다. 따라서
//
//     |Δpitch| < 0.05  ⟺  Δpitch == 0
//
//   이고, epsilon 을 0.005 까지 조여도 걸러지는 표본이 없다.
//   8개 세션 전부에서 e050 == e005 였다.
//
//   그리고 반동 제어 중에는 22카운트가 마우스 이동량의 최빈값이다.
//   즉 잘하는 플레이어일수록 frozen 비율이 구조적으로 높아진다.
//   비율 지표로는 정상 22.0% / 핵 64.8% 까지 좁혀졌다(1.5배).
//
//   갈리는 것은 연속 길이다.
//
//     정상  avg_run 1.05~1.36  우연히 22카운트를 맞춘 단발 사고
//     핵    avg_run 5~33       마우스를 안 만지는 지속 상태
//
//   사람은 22카운트를 연달아 맞출 수 없다. 핵은 손을 떼기만 하면 된다.
//   같은 "고정"이라도 사람은 사고고 핵은 상태다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 리셋이 아니라 감쇠를 쓰는 이유 ★
//
//   frozen 이 끊기면 카운터를 0 으로 되돌리는 것이 단순하다. 그러면
//   20발마다 마우스를 한 번 툭 치는 것으로 빠져나갈 수 있다.
//
//     frozen      → counter += 1
//     not frozen  → counter = max(0, counter - 1)
//
//   감쇠면 그래도 누적된다. 실측 8세션 전부에서 리셋 방식과 같은
//   결과가 나오므로 정상 플레이에 손해가 없다.
//
//  ─────────────────────────────────────────────────────────────────
//  지표 변경 이력
//
//   1차  창 20발의 표본 표준편차, 임계 0.30
//        → match 68 오탐. 정상 최저 0.228 이 핵 값 0.250~0.290 보다
//          낮아 분포가 겹쳤다. 매치 전체 편차(0.850)로 임계를 정하고
//          창 20발 편차(0.228)로 판정한 것이 원인이었다.
//          교훈: 임계는 판정과 같은 단위에서 측정해야 한다.
//
//   2차  창 40발의 frozen 비율, 임계 60%
//        → 조준을 유지한 노리코일이 64.8% 까지 내려오고, 반동 제어가
//          좋은 정상 플레이가 22.0% 까지 올라와 여유가 1.5배로 좁아졌다.
//
//   3차  연속 길이 (현재)
//        → 11배 분리. 창 큐가 필요 없어 구현도 단순해졌다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 이 지표가 잡아낸 게임 로직 버그 (W8.5) ★
//
//   정상 플레이의 max_run 이 2 에서 14 로 뛰어 조사한 결과,
//   WeaponSystem.FinishClientReload 가 서버의 이미 차감된 _reserve 를
//   읽어 마지막 재장전에서 클라 탄약이 0 으로 남는 버그를 찾았다.
//
//     서버  탄창 30발 보유 → 발사 승인 → combat_events 에 기록
//     클라  _clientAmmo = 0 → 반동 미적용
//
//   마지막 탄창 전체가 "반동 없는 발사"로 기록됐고, 이는 노리코일과
//   구분되지 않는다. 클라 탄약 예측을 제거해 해결했다.
//   matches 97, 98 은 그 시기 데이터라 임계 산정에서 제외했다.
//
//   탐지 지표가 없었다면 발견하지 못했을 버그다.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 shot_index >= RecoilRampShots 만 보는가
//
//   램프 구간(0~7발)은 반동이 0.4 에서 1.1 로 발마다 커진다. 고정에
//   필요한 마우스 이동량이 매 발 달라지므로 정상과 핵의 경계가 흐려진다.
//   램프 이후는 1.1 로 평평해 조건이 균일하다.
//
//  왜 차단하지 않는가
//
//   차단은 관리자가 한다(B안 확정). 이 검증기는 근거를 남기는 역할이다.
//
//  알려진 한계
//
//   (1) 반동과 정확히 같은 양을 매번 움직이는 역보정 매크로는 통과한다.
//       pitch 가 계속 변하므로 frozen 으로 세지 않는다. 그런 매크로는
//       궤적이 결정론적이라 W13 에서 Δpitch 분포로 잡는 편이 낫다.
//
//   (2) 서버가 거부한 발사는 계산에 들어오지 않는다. 클라는 그 발사에도
//       반동을 적용하므로 거부가 끼면 Δpitch 가 두 발치를 담는다.
//       고정 판정은 "작은 값"을 찾는 것이라 이 오염은 미탐 방향으로
//       작용한다. 안전한 쪽이다.
//
//   (3) pitch 는 [-89, 89] 로 클램프된다. 천장이나 바닥을 정면으로
//       보면 반동이 잘려 Δpitch 가 0 이 된다. 노리코일과 구별되지
//       않으므로 PitchGuardDeg 로 걸러낸다. 이건 반드시 필요하다.
//
//   (4) 표본 2명, 세션당 판정 110~740 발. 더 쌓을수록 정상 상한이
//       올라갈 수 있다. 다만 여유가 6배라 10 까지 올라가도 안전하다.
// =====================================================================

using UnityEngine;

public static class VRecoil
{
    /// <summary>
    /// 이 각도 미만의 조준각 변화를 "고정"으로 본다.
    ///
    /// pitch 가 0.05도 격자 위에만 존재하므로 이 조건은 사실상
    /// Δpitch == 0 과 같다. 값을 더 내려도 걸러지는 표본이 없고,
    /// 올리면 정상 플레이의 미세 조정까지 고정으로 세게 된다.
    /// </summary>
    public const float FreezeEpsilonDeg = 0.05f;

    /// <summary>
    /// 고정 연속 카운터가 이 값에 닿으면 위반.
    /// 실측 정상 상한 4 / 핵 하한 45.
    /// </summary>
    public const int RunLimit = 25;

    /// <summary>
    /// 히스테리시스. 카운터가 이 아래로 내려가야 다음 에피소드를
    /// 다시 보고한다. 없으면 임계를 넘은 뒤 매 발 보고된다.
    /// </summary>
    public const int RunClearLimit = 8;

    /// <summary>
    /// 이 각도를 넘는 조준에서는 표본을 버린다.
    /// pitch 가 ±89 로 클램프되면 반동이 잘려 Δpitch 가 0 이 되고,
    /// 노리코일과 구별되지 않는다.
    /// </summary>
    public const float PitchGuardDeg = 85f;

    public const string CODE = "V-RECOIL-01";

    /// <summary>월핵과 같은 급. 반동 조작은 명백한 클라이언트 변조다.</summary>
    public const int Severity = 3;

    /// <summary>두 발 사이에 조준점이 움직이지 않았는가.</summary>
    public static bool IsFrozen(float deltaPitch)
        => Mathf.Abs(deltaPitch) < FreezeEpsilonDeg;
}


/// <summary>
/// 플레이어 한 명분의 노리코일 검증기. 서버에서만 인스턴스를 만든다.
///
/// 상태는 정수 카운터 하나와 직전 pitch 뿐이다. 창 큐가 필요 없다.
/// </summary>
public class RecoilValidator
{
    /// <summary>고정 연속 카운터. frozen 이면 +1, 아니면 -1 (0 하한).</summary>
    private int _run;

    private float _lastPitch;
    private bool _hasLast;

    /// <summary>이번 에피소드에서 이미 보고했는가.</summary>
    private bool _reported;

    // --- 계측 ---
    // V-TIME 에서 배운 것: 분모가 없으면 위반 건수를 해석할 수 없다.

    /// <summary>판정에 들어간 발 수. 고정 비율의 분모.</summary>
    public int TotalSamples { get; private set; }

    /// <summary>고정으로 센 발 수.</summary>
    public int FrozenSamples { get; private set; }

    /// <summary>연사가 끊겨 기준 pitch 를 버린 횟수.</summary>
    public int BurstBreaks { get; private set; }

    /// <summary>램프 구간이라 건너뛴 발 수.</summary>
    public int SkippedRamp { get; private set; }

    /// <summary>클램프 구간이라 버린 발 수.</summary>
    public int SkippedClamp { get; private set; }

    /// <summary>보고한 위반 수.</summary>
    public int Violations { get; private set; }

    /// <summary>
    /// 세션 최고 연속 길이. 임계 튜닝의 직접 근거다.
    /// 위반이 0 건이어도 이 값이 RunLimit 에 접근하면 오탐 직전이다.
    /// </summary>
    public int MaxRun { get; private set; }

    /// <summary>현재 연속 카운터. 보고 시점 상태를 로그에 남길 때 쓴다.</summary>
    public int CurrentRun => _run;

    /// <summary>
    /// 이번 발사를 판정한다.
    ///
    /// 연사가 끊기면 기준 pitch 를 버린다. 버스트 경계를 넘어 차분하면
    /// 그 사이의 시점 이동이 통째로 한 표본이 되어 "고정 아님"으로
    /// 세어진다(미탐 방향).
    ///
    /// 끊김 판정은 두 축을 모두 본다.
    ///   shotIndex == 0        : 서버가 실시간 기준으로 리셋했다
    ///   gapTicks > ResetTicks : 클라가 틱 기준으로 리셋했다
    /// 두 축이 갈라지는 경우가 실측 0.28% 있으므로(W8 Day 1)
    /// 보수적으로 둘 다 끊김으로 취급한다.
    ///
    /// 연속 카운터는 버스트 경계에서 비우지 않는다. 노리코일은 연사를
    /// 끊어도 계속 켜져 있고, 탄창 30발에서 램프 8발을 빼면 버스트
    /// 하나에서 얻는 표본이 22발뿐이라 임계 25 에 닿지 않는다.
    /// </summary>
    /// <param name="shotIndex">서버가 센 이번 발의 연사 인덱스. 0 이 첫 발.</param>
    /// <param name="pitch">이번 입력의 조준각. 반동이 이미 반영된 값이다.</param>
    /// <param name="gapTicks">직전 승인 발사와의 클라 틱 차. 첫 발은 음수.</param>
    /// <param name="run">판정 시점의 연속 길이. 판정하지 않았으면 -1.</param>
    /// <returns>이번 호출에서 위반을 보고해야 하면 true.</returns>
    public bool TryEvaluate(int shotIndex, float pitch, int gapTicks, out int run)
    {
        run = -1;

        // --- 연사 경계 ---
        bool burstBreak =
            shotIndex == 0 ||
            gapTicks < 0 ||
            gapTicks > WeaponConfig.RecoilResetTicks;

        if (burstBreak)
        {
            if (_hasLast) BurstBreaks++;
            _lastPitch = pitch;
            _hasLast = true;
            return false;
        }

        if (!_hasLast)
        {
            _lastPitch = pitch;
            _hasLast = true;
            return false;
        }

        float deltaPitch = pitch - _lastPitch;
        float prevPitch = _lastPitch;
        _lastPitch = pitch;

        // --- 램프 구간 제외 ---
        // 반동이 0.4 에서 1.1 로 커지는 동안은 고정에 필요한 마우스
        // 이동량이 발마다 달라 정상/핵의 경계가 흐려진다.
        if (shotIndex < WeaponConfig.RecoilRampShots)
        {
            SkippedRamp++;
            return false;
        }

        // --- 클램프 구간 제외 ---
        // pitch 가 ±89 에 붙으면 반동이 잘려 Δpitch 가 0 이 된다.
        // 노리코일과 구별되지 않으므로 반드시 버려야 한다.
        if (Mathf.Abs(pitch) > VRecoil.PitchGuardDeg ||
            Mathf.Abs(prevPitch) > VRecoil.PitchGuardDeg)
        {
            SkippedClamp++;
            return false;
        }

        // --- 판정 ---
        bool frozen = VRecoil.IsFrozen(deltaPitch);

        TotalSamples++;
        if (frozen) FrozenSamples++;

        // 리셋이 아니라 감쇠다. 20발마다 마우스를 한 번 치는 것으로
        // 빠져나가지 못하게 한다.
        _run = frozen ? _run + 1 : Mathf.Max(0, _run - 1);

        if (_run > MaxRun) MaxRun = _run;
        run = _run;

        // --- 히스테리시스 ---
        if (_reported)
        {
            if (_run <= VRecoil.RunClearLimit) _reported = false;
            return false;
        }

        if (_run < VRecoil.RunLimit) return false;

        _reported = true;
        Violations++;
        return true;
    }

    /// <summary>
    /// 세션 누적 계측. 서버 로그로 남겨 Loki 에서 조회한다.
    ///
    /// 읽는 법
    ///   samples 가 한 자리면 8발 이상 이어지는 연사가 없었다는 뜻이다.
    ///   그 세션의 V-RECOIL 결과는 의미가 없다.
    ///
    ///   maxRun 이 판정에 직접 닿는 값이다. 실측 정상 2~4 / 핵 45~290.
    ///   정상 세션에서 이 값이 10 을 넘으면 원인을 조사해야 한다.
    ///   W8.5 에서 14 가 나왔을 때 게임 로직 버그가 드러났다.
    ///
    ///   frozen / samples 는 참고용이다. 이 비율만으로는 정상 22% 와
    ///   핵 64% 가 1.5배까지 좁혀져 판정 근거로 쓸 수 없다.
    /// </summary>
    public string StatsLine()
    {
        float pct = TotalSamples > 0 ? 100f * FrozenSamples / TotalSamples : 0f;
        return $"samples={TotalSamples} frozen={FrozenSamples} ({pct:F1}%) " +
               $"violations={Violations} maxRun={MaxRun} run={_run} " +
               $"bursts={BurstBreaks} skipRamp={SkippedRamp} skipClamp={SkippedClamp}";
    }

    /// <summary>
    /// 리스폰 시 호출한다. 카운터와 기준 pitch 를 버린다.
    ///
    /// 리스폰은 조준각의 불연속점이다. 사망 전 마지막 발사와 부활 후
    /// 첫 발사를 차분하면 그 사이의 시점 이동이 한 표본이 된다.
    ///
    /// 누적 계측과 MaxRun 은 유지한다. 매치 단위 근거이기 때문이다.
    /// (V-TIME 에서 리스폰마다 이력을 비워 반복 판정이 죽었던 것과
    ///  같은 실수를 반복하지 않는다.)
    /// </summary>
    public void ResetForRespawn()
    {
        _run = 0;
        _hasLast = false;
        _reported = false;
    }

    /// <summary>매치 경계에서만 호출한다. 계측까지 전부 버린다.</summary>
    public void Reset()
    {
        _run = 0;
        _hasLast = false;
        _reported = false;
        TotalSamples = 0;
        FrozenSamples = 0;
        BurstBreaks = 0;
        SkippedRamp = 0;
        SkippedClamp = 0;
        Violations = 0;
        MaxRun = 0;
    }
}