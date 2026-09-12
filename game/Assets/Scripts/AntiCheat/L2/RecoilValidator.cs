// =====================================================================
//  RecoilValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/RecoilValidator.cs
//
//  V-RECOIL-01 : 노리코일 검증 (L2, 서버 권위)
//
//  무엇을 재는가
//   연속된 두 발 사이에 플레이어가 실제로 움직인 조준각이다.
//
//     comp[n] = (pitch[n] - pitch[n-1]) + GetRecoil(n).y
//
//   클라이언트는 InputPayload 를 만들기 직전에 반동을 시야에 적용한다
//   (PlayerController.GatherInput). 그래서 pitch[n] 에는 이미
//   GetRecoil(n).y 가 빠져 있다. 다시 더하면 반동 성분이 상쇄되고
//   순수 마우스 이동량만 남는다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 평균이 아니라 편차를 본다 ★
//
//   실측(match 63/64/65, 각 600~800발)이 이유를 보여준다.
//
//     match 63 clean   comp_mean 1.089   comp_sd 0.850
//     match 64 clean   comp_mean 1.084   comp_sd 0.870
//     match 65 cheat   comp_mean 1.097   comp_sd 0.096
//
//   평균이 세 매치 모두 1.08~1.10 으로 같다. 잘하는 플레이어는 반동을
//   거의 전부 상쇄하므로, "얼마나 상쇄했는가"로는 핵과 구분되지 않는다.
//
//   갈라지는 것은 편차다. 사람은 반동을 손으로 따라가느라 매 발
//   흔들린다. 노리코일은 반동이 시야에 적용되지 않으므로 마우스를
//   움직일 필요가 없고, comp 가 GetRecoil(n).y 자체로 수렴한다.
//   그 값은 램프 이후 1.1 로 고정이라 편차가 거의 0이 된다.
//
//   9배 차이다. 이 지표는 평균을 일부러 버리고 편차만 본다.
//
//  ─────────────────────────────────────────────────────────────────
//  임계 근거
//
//     정상 하한   0.850  (match 63)
//     핵 상한     0.290  (match 62 의 노리코일 구간, 조준을 많이 움직인 경우)
//     핵 최저     0.096  (match 65)
//
//   임계 0.30 이면 정상 쪽으로 2.8배, 핵 쪽으로 3배 여유가 있다.
//   0.50 은 정상 하한과 1.7배 차이뿐이라 손이 안정적인 플레이어에게
//   위험하다.
//
//   ※ 실측 표본이 동일 플레이어 1명이다. match 57 기준 개인차가
//     최대 2.5배(1.124 vs 2.153)이므로 0.850 은 잠정 하한이다.
//     다른 플레이어로 검증한 뒤 확정할 것.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 차단하지 않는가
//
//   차단은 관리자가 한다(B안 확정). 이 검증기는 근거를 남기는 역할이다.
//   또한 편차가 낮다는 것만으로는 조작을 증명하지 못한다. 반동이 약한
//   구간에서 마우스를 거의 안 움직이면 사람도 낮게 나올 수 있다.
//   창 20발을 요구하는 이유가 그것이다.
//
//  ─────────────────────────────────────────────────────────────────
//  알려진 한계
//
//   (1) 서버가 거부한 발사는 이 계산에 들어오지 않는다. 그런데 클라는
//       그 발사에도 반동을 적용하고 _clientShotIndex 를 올린다.
//       거부가 끼면 서버의 shotIndex 와 클라가 실제 적용한 인덱스가
//       어긋나 comp 가 오염된다.
//       램프 이후에는 GetRecoil 이 1.1 로 평평해 영향이 작고,
//       W8 Day 1 실측에서 정상 플레이의 거부는 0건이었다.
//
//   (2) pitch 는 [-89, 89] 로 클램프된다. 천장이나 바닥을 정면으로
//       보면 반동이 잘려 comp 가 왜곡된다. PitchGuardDeg 로 걸러낸다.
//
//   (3) 수평 반동은 쓰지 않는다. GetRecoil().x 는 결정론적이지만
//       expected_recoil_pitch 만 텔레메트리에 남고 누적하면 0 근처로
//       상쇄되어 신호가 약하다. 세로축만으로 9배가 갈리므로 충분하다.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

public static class VRecoil
{
    /// <summary>판정에 필요한 표본 수. 창이 찰 때까지 평가하지 않는다.</summary>
    public const int WindowSize = 20;

    /// <summary>
    /// 이 표본 표준편차 미만이면 위반.
    /// 실측: 정상 0.850~2.153, 노리코일 0.096~0.290.
    /// </summary>
    public const float SdThreshold = 0.30f;

    /// <summary>
    /// 히스테리시스. 편차가 이 값을 넘어야 다음 에피소드를 다시 보고한다.
    /// 창이 굴러가는 구조라 이게 없으면 한 번 걸린 뒤 매 발 보고된다.
    /// </summary>
    public const float SdClearThreshold = 0.60f;

    /// <summary>
    /// 이 각도를 넘는 조준에서는 표본을 버린다.
    /// pitch 가 ±89 로 클램프되어 반동이 잘리는 구간이다.
    /// </summary>
    public const float PitchGuardDeg = 85f;

    public const string CODE = "V-RECOIL-01";

    /// <summary>월핵과 같은 급. 반동 조작은 명백한 클라이언트 변조다.</summary>
    public const int Severity = 3;

    /// <summary>편차가 인간 범위를 밑도는가.</summary>
    public static bool IsSuspicious(float sd) => sd >= 0f && sd < SdThreshold;
}


/// <summary>
/// 플레이어 한 명분의 노리코일 검증기. 서버에서만 인스턴스를 만든다.
/// </summary>
public class RecoilValidator
{
    private readonly Queue<float> _window = new();

    private float _lastPitch;
    private bool _hasLast;

    /// <summary>이번 에피소드에서 이미 보고했는가.</summary>
    private bool _reported;

    // --- 계측 ---
    // V-TIME 에서 배운 것: 분모가 없으면 위반 건수를 해석할 수 없다.

    /// <summary>창에 들어간 표본 수.</summary>
    public int TotalSamples { get; private set; }

    /// <summary>창이 차서 실제로 판정한 횟수.</summary>
    public int TotalEvaluated { get; private set; }

    /// <summary>연사가 끊겨 창을 비운 횟수.</summary>
    public int BurstBreaks { get; private set; }

    /// <summary>클램프 구간이라 버린 표본 수.</summary>
    public int SkippedClamp { get; private set; }

    /// <summary>보고한 위반 수.</summary>
    public int Violations { get; private set; }

    /// <summary>마지막 판정값. -1 이면 아직 판정 없음.</summary>
    public float LastSd { get; private set; } = -1f;

    /// <summary>세션 최저 편차. 임계 튜닝의 근거가 된다.</summary>
    public float MinSd { get; private set; } = -1f;

    /// <summary>
    /// 이번 발사를 표본에 넣고 필요하면 판정한다.
    ///
    /// 연사가 끊기면 창을 비운다. 버스트 경계를 넘어 pitch 를 차분하면
    /// 그 사이의 마우스 이동이 전부 한 표본에 뭉쳐 들어가 편차를
    /// 의미 없이 키운다.
    ///
    /// 끊김 판정은 두 축을 모두 본다.
    ///   shotIndex == 0        : 서버가 실시간 기준으로 리셋했다
    ///   gapTicks > ResetTicks : 클라가 틱 기준으로 리셋했다
    /// 한쪽만 봐도 대부분 맞지만, 두 축이 갈라지는 경우가 실측 0.28%
    /// 있으므로(W8 Day 1) 보수적으로 둘 다 끊김으로 취급한다.
    /// </summary>
    /// <param name="shotIndex">서버가 센 이번 발의 연사 인덱스. 0 이 첫 발.</param>
    /// <param name="pitch">이번 입력의 조준각. 반동이 이미 반영된 값이다.</param>
    /// <param name="gapTicks">직전 승인 발사와의 클라 틱 차. 첫 발은 음수.</param>
    /// <param name="sd">판정한 표본 표준편차. 판정하지 않았으면 -1.</param>
    /// <returns>이번 호출에서 위반을 보고해야 하면 true.</returns>
    public bool TryEvaluate(int shotIndex, float pitch, int gapTicks, out float sd)
    {
        sd = -1f;

        // --- 연사 경계 ---
        bool burstBreak =
            shotIndex == 0 ||
            gapTicks < 0 ||
            gapTicks > WeaponConfig.RecoilResetTicks;

        if (burstBreak)
        {
            if (_window.Count > 0) BurstBreaks++;
            _window.Clear();
            _reported = false;
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

        // --- 클램프 구간 제외 ---
        // pitch 가 ±89 에 붙으면 반동이 잘려 comp 가 실제보다 작아진다.
        // 기준선을 잃지 않도록 _lastPitch 는 갱신하되 표본은 버린다.
        if (Mathf.Abs(pitch) > VRecoil.PitchGuardDeg ||
            Mathf.Abs(_lastPitch) > VRecoil.PitchGuardDeg)
        {
            SkippedClamp++;
            _lastPitch = pitch;
            return false;
        }

        // --- 표본 ---
        // pitch 에서 반동 성분을 되돌려 순수 마우스 이동량만 남긴다.
        float comp = (pitch - _lastPitch) + WeaponConfig.GetRecoil(shotIndex).y;
        _lastPitch = pitch;

        _window.Enqueue(comp);
        while (_window.Count > VRecoil.WindowSize) _window.Dequeue();
        TotalSamples++;

        if (_window.Count < VRecoil.WindowSize) return false;

        sd = SampleStdDev(_window);
        LastSd = sd;
        TotalEvaluated++;

        if (MinSd < 0f || sd < MinSd) MinSd = sd;

        // --- 히스테리시스 ---
        // 창이 한 칸씩 굴러가므로 플래그 없이는 같은 구간이 매 발 보고된다.
        if (_reported)
        {
            if (sd > VRecoil.SdClearThreshold) _reported = false;
            return false;
        }

        if (!VRecoil.IsSuspicious(sd)) return false;

        _reported = true;
        Violations++;
        return true;
    }

    /// <summary>표본 표준편차(n-1). SQL 의 STDDEV_SAMP 와 같은 정의다.</summary>
    private static float SampleStdDev(Queue<float> values)
    {
        int n = values.Count;
        if (n < 2) return 0f;

        float sum = 0f;
        foreach (float v in values) sum += v;
        float mean = sum / n;

        float sq = 0f;
        foreach (float v in values)
        {
            float d = v - mean;
            sq += d * d;
        }
        return Mathf.Sqrt(sq / (n - 1));
    }

    /// <summary>
    /// 세션 누적 계측. 서버 로그로 남겨 Loki 에서 조회한다.
    ///
    /// 읽는 법
    ///   evaluated 가 0 이면 창이 한 번도 안 찼다는 뜻이다. 20발 연속
    ///   연사가 없었거나 버스트가 너무 짧다. 그 세션의 V-RECOIL 결과는
    ///   의미가 없다.
    ///   minSd 는 정상 플레이에서도 기록된다. 이 값이 임계(0.30)에
    ///   가까워지면 임계를 낮춰야 한다는 신호다.
    /// </summary>
    public string StatsLine()
        => $"samples={TotalSamples} evaluated={TotalEvaluated} " +
           $"violations={Violations} bursts={BurstBreaks} " +
           $"skipClamp={SkippedClamp} " +
           $"lastSd={(LastSd < 0f ? "-" : LastSd.ToString("F3"))} " +
           $"minSd={(MinSd < 0f ? "-" : MinSd.ToString("F3"))}";

    /// <summary>
    /// 리스폰 시 호출한다. 창과 기준 pitch 만 버린다.
    ///
    /// 리스폰은 조준각의 불연속점이다. 사망 전 마지막 발사와 부활 후
    /// 첫 발사를 차분하면 그 사이의 시점 이동이 통째로 한 표본이 된다.
    ///
    /// 누적 카운터와 MinSd 는 유지한다. 매치 단위 근거이기 때문이다.
    /// (V-TIME 에서 리스폰마다 _history 를 비워 반복 판정이 죽었던
    ///  것과 같은 실수를 반복하지 않는다.)
    /// </summary>
    public void ResetForRespawn()
    {
        _window.Clear();
        _hasLast = false;
        _reported = false;
    }

    /// <summary>매치 경계에서만 호출한다. 계측까지 전부 버린다.</summary>
    public void Reset()
    {
        _window.Clear();
        _hasLast = false;
        _reported = false;
        TotalSamples = 0;
        TotalEvaluated = 0;
        BurstBreaks = 0;
        SkippedClamp = 0;
        Violations = 0;
        LastSd = -1f;
        MinSd = -1f;
    }
}
