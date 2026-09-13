// =====================================================================
//  RecoilValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/RecoilValidator.cs
//
//  V-RECOIL-01 : 노리코일 검증 (L2, 서버 권위)
//
//  무엇을 재는가
//   연속된 두 발 사이에 조준점이 "전혀 움직이지 않은" 횟수를 센다.
//
//     frozen  ⟺  |pitch[n] - pitch[n-1]| < FreezeEpsilonDeg
//
//   반동은 클라이언트에서 InputPayload 생성 직전에 시야에 적용된다
//   (PlayerController.GatherInput). 따라서 이 조건이 뜻하는 바는
//   두 진영에서 완전히 다르다.
//
//     정상      반동이 조준점을 밀어 올린다. 가만히 있으면 pitch 가
//               반동만큼 변한다. 조준점을 고정하려면 마우스를 반동과
//               "정확히 같은 양"만큼 반대로 움직여야 한다. 어렵다.
//     노리코일  밀어 올릴 반동이 없다. 손을 떼면 그대로 고정된다.
//
//   즉 이 지표는 "반동을 얼마나 잘 상쇄했는가"가 아니라
//   "상쇄할 반동이 애초에 있었는가"를 묻는다.
//
//  ─────────────────────────────────────────────────────────────────
//  실측 (W8 Day 2, shot_index >= 8 구간)
//
//     match 63  clean    42 / 551    7.6%
//     match 64  clean    34 / 736    4.6%
//     match 68  clean    35 / 401    8.7%
//     match 71  clean    43 / 479    9.0%
//     match 65  cheat   553 / 593   93.3%
//     match 72  cheat   320 / 494   64.8%
//
//   정상 4.6~9.0% / 핵 64.8~93.3%. 겹치지 않고 7배 이상 벌어진다.
//   정상 네 세션이 좁게 모여 있다는 점이 중요하다. 임계를 정할 수 있는
//   조건이 바로 그것이다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 왜 표본 표준편차를 버렸는가 (실패 기록) ★
//
//   1차 설계는 comp = (Δpitch + GetRecoil(n).y) 의 표본 표준편차였다.
//
//     match 65 cheat (조준 고정)   comp_sd 0.096  ← 정상 대비 9배
//     match 72 cheat (조준 유지)   comp_sd 0.437  ← 정상 대비 1.3배
//     match 71 clean               comp_sd 0.570
//     match 68 clean               창 최저 0.228  ← 오탐 발생
//
//   조준을 유지한 노리코일에서 분리가 무너졌다. 이유는 구조적이다.
//
//     정상      comp = 조준이동 + 반동상쇄오차
//     노리코일  comp = 조준이동 + 0
//
//   차이는 "반동상쇄오차" 하나뿐인데, 조준이동의 분산이 크면 그것이
//   전체를 덮는다. 분산은 분포의 모양을 보는 통계량이라, 고정 구간과
//   조준 구간이 섞이면 중간값이 되어 희석된다.
//
//   같은 match 72 를 고정 빈도로 보면 64.8% 로 살아 있다. 개별 발의
//   상태를 세기 때문에, 조준하는 발은 분모에 들어갈 뿐 고정된 발의
//   개수를 지우지 못한다.
//
//   교훈 두 가지.
//     - 신호가 "분포의 모양"이 아니라 "특정 상태의 빈도"일 때는
//       분산 대신 계수를 써야 한다.
//     - 임계는 판정과 같은 단위에서 측정해야 한다. 1차 설계는 매치
//       전체 편차(0.850)로 임계를 정하고 창 20발 편차(0.228)로
//       판정했다. 그것이 match 68 오탐의 직접 원인이었다.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 shot_index >= RecoilRampShots 만 보는가
//
//   램프 구간(0~7발)은 반동이 0.4 에서 1.1 로 발마다 커진다. "조준점
//   고정"의 난이도가 발마다 다르므로 정상과 핵의 경계가 흐려진다.
//   램프 이후는 1.1 로 평평해 조건이 균일하다.
//
//   표본이 줄지만 문제없다. 탄창 30발이 구현돼도 한 탄창에 22발이
//   쌓이므로 두 탄창이면 창 40발이 찬다.
//
//  ─────────────────────────────────────────────────────────────────
//  임계 근거
//
//     FreezeEpsilonDeg  0.05
//       마우스 센서 해상도와 float 오차를 흡수하는 최소값이다.
//       키우면 정상 플레이의 미세 조정까지 "고정"으로 세게 된다.
//
//     FreezeRatio  0.60  (창 40발 중 24발)
//       정상 상한 9.0% / 핵 하한 64.8%.
//       핵 쪽 여유는 좁지만 정상 쪽으로 6.7배 여유가 있다.
//       오탐을 피하는 쪽을 택한다. 차단이 아니라 근거 제출이 목적이고,
//       놓친 핵은 L1/L3 과 W13 Trust Score 가 다시 본다.
//
//     ※ 실측 표본이 동일 플레이어 1명이다. 개인차 검증 필요.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 차단하지 않는가
//
//   차단은 관리자가 한다(B안 확정). 이 검증기는 근거를 남기는 역할이다.
//
//  ─────────────────────────────────────────────────────────────────
//  알려진 한계
//
//   (1) 반동과 정확히 같은 양을 매번 움직이는 스크립트(반동 역보정
//       매크로)는 이 지표를 통과한다. 그 경우 pitch 는 계속 변하므로
//       고정으로 세지 않는다. 다만 그런 매크로는 궤적이 결정론적이라
//       W13 에서 comp 분포로 잡는 편이 낫다.
//
//   (2) 서버가 거부한 발사는 계산에 들어오지 않는다. 클라는 그 발사에도
//       반동을 적용하므로 거부가 끼면 Δpitch 가 두 발치를 담는다.
//       고정 판정은 "작은 값"을 찾는 것이라 이 오염은 오탐이 아니라
//       미탐 방향으로 작용한다. 안전한 쪽이다.
//
//   (3) pitch 는 [-89, 89] 로 클램프된다. 천장이나 바닥을 정면으로
//       보면 반동이 잘려 Δpitch 가 0 이 된다. 노리코일과 구별되지
//       않으므로 PitchGuardDeg 로 걸러낸다. 이건 반드시 필요하다.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

public static class VRecoil
{
    /// <summary>
    /// 이 각도 미만의 조준각 변화를 "고정"으로 본다.
    /// 마우스 센서 해상도와 float 오차를 흡수하는 최소값.
    /// </summary>
    public const float FreezeEpsilonDeg = 0.05f;

    /// <summary>
    /// 판정 창. shot_index >= WeaponConfig.RecoilRampShots 인 발만
    /// 여기에 들어간다. 반동이 평평한 구간이라 고정 난이도가 균일하다.
    /// </summary>
    public const int WindowSize = 40;

    /// <summary>
    /// 창 안에서 이 비율 이상이 고정이면 위반.
    /// 실측: 정상 4.6~9.0% / 노리코일 64.8~93.3%.
    /// </summary>
    public const float FreezeRatio = 0.60f;

    /// <summary>보고 임계. 창 40발 중 24발.</summary>
    public static int FreezeLimit => Mathf.CeilToInt(WindowSize * FreezeRatio);

    /// <summary>
    /// 히스테리시스. 고정 수가 이 아래로 내려가야 다음 에피소드를
    /// 다시 보고한다. 창이 한 칸씩 굴러가므로 없으면 매 발 보고된다.
    /// 정상 상한(9%)의 약 2배에 해당하는 지점으로 잡는다.
    /// </summary>
    public static int FreezeClearLimit => Mathf.FloorToInt(WindowSize * 0.20f);

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
/// </summary>
public class RecoilValidator
{
    /// <summary>최근 판정 대상 발의 고정 여부. 버스트 경계를 넘어 이어진다.</summary>
    private readonly Queue<bool> _window = new();

    private int _frozenCount;

    private float _lastPitch;
    private bool _hasLast;

    /// <summary>이번 에피소드에서 이미 보고했는가.</summary>
    private bool _reported;

    // --- 계측 ---
    // V-TIME 에서 배운 것: 분모가 없으면 위반 건수를 해석할 수 없다.

    /// <summary>창에 들어간 표본 수. 고정 비율의 분모.</summary>
    public int TotalSamples { get; private set; }

    /// <summary>고정으로 센 발 수. 세션 전체 비율을 여기서 낸다.</summary>
    public int FrozenSamples { get; private set; }

    /// <summary>연사가 끊겨 기준 pitch 를 버린 횟수.</summary>
    public int BurstBreaks { get; private set; }

    /// <summary>램프 구간이라 건너뛴 발 수.</summary>
    public int SkippedRamp { get; private set; }

    /// <summary>클램프 구간이라 버린 발 수.</summary>
    public int SkippedClamp { get; private set; }

    /// <summary>보고한 위반 수.</summary>
    public int Violations { get; private set; }

    /// <summary>세션 최고 고정 비율. 임계 튜닝의 근거가 된다.</summary>
    public float MaxRatio { get; private set; } = -1f;

    /// <summary>창 안의 현재 고정 수. 보고 시점 상태를 로그에 남길 때 쓴다.</summary>
    public int CurrentFrozen => _frozenCount;

    /// <summary>창에 들어찬 표본 수.</summary>
    public int CurrentWindow => _window.Count;

    /// <summary>
    /// 이번 발사를 표본에 넣고 필요하면 판정한다.
    ///
    /// 연사가 끊기면 기준 pitch 를 버린다. 버스트 경계를 넘어 차분하면
    /// 그 사이의 시점 이동이 통째로 한 표본이 되어 "고정 아님"으로
    /// 잘못 세어진다(미탐 방향).
    ///
    /// 끊김 판정은 두 축을 모두 본다.
    ///   shotIndex == 0        : 서버가 실시간 기준으로 리셋했다
    ///   gapTicks > ResetTicks : 클라가 틱 기준으로 리셋했다
    /// 두 축이 갈라지는 경우가 실측 0.28% 있으므로(W8 Day 1)
    /// 보수적으로 둘 다 끊김으로 취급한다.
    ///
    /// 창(_window)은 버스트 경계에서 비우지 않는다. 노리코일은 연사를
    /// 끊어도 계속 켜져 있고, 램프 구간을 빼면 버스트 하나에서 얻는
    /// 표본이 적기 때문이다.
    /// </summary>
    /// <param name="shotIndex">서버가 센 이번 발의 연사 인덱스. 0 이 첫 발.</param>
    /// <param name="pitch">이번 입력의 조준각. 반동이 이미 반영된 값이다.</param>
    /// <param name="gapTicks">직전 승인 발사와의 클라 틱 차. 첫 발은 음수.</param>
    /// <param name="ratio">판정한 고정 비율. 판정하지 않았으면 -1.</param>
    /// <returns>이번 호출에서 위반을 보고해야 하면 true.</returns>
    public bool TryEvaluate(int shotIndex, float pitch, int gapTicks, out float ratio)
    {
        ratio = -1f;

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
        // 반동이 0.4 에서 1.1 로 커지는 동안은 고정 난이도가 발마다 달라
        // 정상/핵의 경계가 흐려진다. 평평한 구간만 본다.
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

        // --- 표본 ---
        bool frozen = VRecoil.IsFrozen(deltaPitch);

        TotalSamples++;
        if (frozen) FrozenSamples++;

        _window.Enqueue(frozen);
        if (frozen) _frozenCount++;

        while (_window.Count > VRecoil.WindowSize)
        {
            if (_window.Dequeue()) _frozenCount--;
        }

        if (_window.Count < VRecoil.WindowSize) return false;

        ratio = (float)_frozenCount / _window.Count;
        if (ratio > MaxRatio) MaxRatio = ratio;

        // --- 히스테리시스 ---
        if (_reported)
        {
            if (_frozenCount <= VRecoil.FreezeClearLimit) _reported = false;
            return false;
        }

        if (_frozenCount < VRecoil.FreezeLimit) return false;

        _reported = true;
        Violations++;
        return true;
    }

    /// <summary>
    /// 세션 누적 계측. 서버 로그로 남겨 Loki 에서 조회한다.
    ///
    /// 읽는 법
    ///   samples 가 창(40)보다 작으면 판정이 한 번도 안 돌았다.
    ///   8발 이상 이어지는 연사가 부족했다는 뜻이며, 그 세션의
    ///   V-RECOIL 결과는 의미가 없다.
    ///
    ///   frozen / samples 가 세션 전체 고정 비율이다.
    ///   실측 정상 4.6~9.0% / 노리코일 64.8~93.3%.
    ///   정상 세션에서 이 값이 20% 를 넘으면 임계를 재검토한다.
    ///
    ///   maxRatio 는 창 단위 최고값이다. 정상 플레이에서 이 값이
    ///   FreezeRatio(0.60)에 접근하면 오탐 직전이라는 신호다.
    ///   위반이 0 건이어도 이 값이 0.5 를 넘으면 임계를 올려야 한다.
    /// </summary>
    public string StatsLine()
    {
        float pct = TotalSamples > 0 ? 100f * FrozenSamples / TotalSamples : 0f;
        return $"samples={TotalSamples} frozen={FrozenSamples} ({pct:F1}%) " +
               $"violations={Violations} bursts={BurstBreaks} " +
               $"skipRamp={SkippedRamp} skipClamp={SkippedClamp} " +
               $"maxRatio={(MaxRatio < 0f ? "-" : MaxRatio.ToString("F3"))} " +
               $"now={_frozenCount}/{_window.Count}";
    }

    /// <summary>
    /// 리스폰 시 호출한다. 창과 기준 pitch 를 버린다.
    ///
    /// 리스폰은 조준각의 불연속점이다. 사망 전 마지막 발사와 부활 후
    /// 첫 발사를 차분하면 그 사이의 시점 이동이 한 표본이 된다.
    ///
    /// 누적 카운터와 MaxRatio 는 유지한다. 매치 단위 근거이기 때문이다.
    /// (V-TIME 에서 리스폰마다 이력을 비워 반복 판정이 죽었던 것과
    ///  같은 실수를 반복하지 않는다.)
    /// </summary>
    public void ResetForRespawn()
    {
        _window.Clear();
        _frozenCount = 0;
        _hasLast = false;
        _reported = false;
    }

    /// <summary>매치 경계에서만 호출한다. 계측까지 전부 버린다.</summary>
    public void Reset()
    {
        _window.Clear();
        _frozenCount = 0;
        _hasLast = false;
        _reported = false;
        TotalSamples = 0;
        FrozenSamples = 0;
        BurstBreaks = 0;
        SkippedRamp = 0;
        SkippedClamp = 0;
        Violations = 0;
        MaxRatio = -1f;
    }
}