// =====================================================================
//  ReactionTimeValidator.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/ReactionTimeValidator.cs
//
//  V-TIME-01 : 반응시간 검증 (L2, 서버 권위)
//
//  무엇을 재는가
//   SPOT(적이 A 화면에 뜬 순간)부터 A 의 첫 발사가 서버에 도착하기까지의
//   시간이다. 트리거봇은 조준선에 적이 들어오는 순간 발사하므로
//   사람의 시각-운동 반응 하한(150~200ms)을 크게 밑돈다.
//
//  ─────────────────────────────────────────────────────────────────
//  정규화
//
//   VisibilitySystem 은 되감은 기하로 가시성을 판정한다. 즉 spotTime 은
//   "서버가 안 순간"이 아니라 "A 화면에 그려진 순간"이다.
//   RTT/2 다운링크와 보간 지연이 이미 반영돼 있으므로 남은 것은
//   업링크뿐이다.
//
//     raw      = fireArrival - spotTime = reaction + RTT/2
//     adjusted = raw - RTT/2
//
//  ─────────────────────────────────────────────────────────────────
//  20Hz 양자화 보정
//
//   가시성 루프가 20Hz 라 실제 시야 확보보다 최대 50ms 늦게 SPOT 을
//   잡는다. spotTime 이 늦어지면 반응시간은 그만큼 짧게 측정된다.
//   그래서 판정에 불확실성을 더해 실효 임계를 60ms 로 낮춘다.
//
//     adjusted + SpotQuantizationMs < ThresholdMs
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W7 Day 5 실측으로 드러난 오탐 원인과 수정 ★
//
//   S3(match 44, 정상 플레이 1087발)에서 13건이 발화했다. 치트는
//   한 번도 쓰지 않았으므로 전부 오탐이다. 원인은 두 가지였다.
//
//   (1) 연사 도중 발사를 "반응"으로 쟀다
//
//       raw_ms   shot_index
//         4.0        15      ← 연사 16번째 발
//         6.0        10
//        10.0        19      ← 연사 20번째 발
//        16.0        13
//
//       SPOT 하나당 첫 발만 잰다는 기존 규칙은 "같은 spotId 를 두 번
//       재지 않는다"였다. 그런데 교전 중 상대가 잠깐 시야에서 벗어났다
//       돌아오면(ForgetSec 0.5초) 새 spotId 가 발급된다. 그 순간
//       사수는 이미 발사 버튼을 누르고 있으므로, 다음 발사 게이트가
//       열리는 즉시 총알이 나가 4ms 가 측정된다.
//       → shotIndex > 0 이면 트리거를 이미 당기고 있었다는 뜻이므로
//         측정하지 않는다.
//
//   (2) 재조우를 첫 조우로 쟀다
//
//       raw_ms   shot_index
//         5.0         0
//        20.0         0
//        53.0         0
//
//       연사를 잠깐 멈췄다 재개한 경우다. shotIndex 는 0 으로 리셋됐지만
//       총구는 계속 상대를 향하고 있었다. 이미 교전 중인 상대의 재등장은
//       반응 대상이 아니다.
//       → 최근 EngagementWindowSec 안에 같은 표적에게 쏜 적이 있으면
//         측정하지 않는다.
//
//   탐지력 손실
//     두 게이트 모두 측정 횟수를 줄이기만 하고 늘리지 않는다.
//     트리거봇의 신호는 "처음 조준선에 들어온 순간 발사"이므로
//     첫 조우에서 여전히 잡힌다. 지속 교전 중의 트리거봇은 놓치지만,
//     그 구간은 사람과 구분되는 신호가 애초에 희박하다.
//
//   ※ 수정 후 F11(트리거봇) 하네스로 여전히 탐지되는지 반드시 확인할 것.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 차단하지 않는가
//
//   확률적 지표다. 코너를 미리 겨누고 있다가 적이 나타나면 사람도
//   0ms 에 가까운 반응이 나온다. 정당한 플레이다.
//   그래서 단발은 severity 1 로 기록만 하고, 최근 20회 중 5회 이상
//   반복될 때 severity 3 으로 올린다. 차단은 하지 않는다.
//   W13 Trust Score 의 입력으로 쓴다.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

public static class VTime
{
    /// <summary>인간 반응 하한. 이보다 빠르면 사람이 아니다.</summary>
    public const int ThresholdMs = 110;

    /// <summary>
    /// SPOT 검출 주기에서 오는 측정 불확실성(ms).
    /// VLos.TickDivisor(3) x 틱 간격(16.7ms) = 50ms.
    /// 이 값을 측정치에 더해 판정하므로 실효 임계는 60ms 가 된다.
    /// </summary>
    public const int SpotQuantizationMs = 50;

    /// <summary>
    /// 같은 표적에게 이 시간 안에 발사한 적이 있으면 "교전 중"으로 본다.
    /// 그 상대의 재등장은 첫 조우가 아니므로 반응시간을 재지 않는다.
    ///
    /// W7 Day 5 실측 근거: 오탐 13건 중 3건이 shotIndex=0 이면서
    /// 직전 수 초 내에 같은 상대와 교전 중이던 경우였다.
    /// VisibilitySystem.ForgetSec(0.5초)보다 충분히 길어야 한다.
    /// </summary>
    public const float EngagementWindowSec = 3.0f;

    /// <summary>최근 몇 회 교전을 보는가.</summary>
    public const int WindowSize = 20;

    /// <summary>창 안에서 이만큼 반복되면 심각도를 올린다.</summary>
    public const int RepeatLimit = 5;

    public const string CODE = "V-TIME-01";

    public const int SeveritySingle = 1;
    public const int SeverityRepeat = 3;
}


/// <summary>
/// 플레이어 한 명분의 반응시간 검증기. 서버에서만 인스턴스를 만든다.
/// </summary>
public class ReactionTimeValidator
{
    private readonly Queue<int> _history = new();

    /// <summary>표적별로 마지막에 측정한 SPOT id. 같은 SPOT 중복 측정을 막는다.</summary>
    private readonly Dictionary<ulong, long> _measured = new();

    /// <summary>
    /// 표적별 마지막 발사 시각(realtime). 재조우 판정에 쓴다.
    /// 측정 여부와 무관하게 발사할 때마다 갱신한다.
    /// </summary>
    private readonly Dictionary<ulong, float> _lastFireAt = new();

    public int TotalMeasured { get; private set; }

    /// <summary>연사 도중이라 건너뛴 횟수. 튜닝 확인용.</summary>
    public int SkippedBurst { get; private set; }

    /// <summary>교전 중 재조우라 건너뛴 횟수. 튜닝 확인용.</summary>
    public int SkippedReengage { get; private set; }

    /// <summary>
    /// 이번 발사의 반응시간을 잰다.
    ///
    /// 다음 경우에는 재지 않는다 (전부 W7 Day 5 실측 근거).
    ///   - 연사 도중 (shotIndex > 0) : 트리거를 이미 당기고 있었다
    ///   - 같은 SPOT 재측정
    ///   - 최근 EngagementWindowSec 안에 같은 표적에게 발사 : 재조우
    /// </summary>
    /// <param name="targetId">표적 clientId</param>
    /// <param name="spotId">VisibilitySystem 이 발급한 SPOT id</param>
    /// <param name="shotIndex">이번 발의 연사 인덱스. 0 이 연사 첫 발.</param>
    /// <param name="now">Time.realtimeSinceStartup (발사 입력이 서버에 도착한 시각)</param>
    /// <param name="spotTime">SPOT 성립 시각</param>
    /// <param name="rttMs">서버 측정 RTT</param>
    /// <param name="adjustedMs">RTT 정규화한 반응시간</param>
    /// <param name="fastCount">최근 창에서 임계 미만이었던 횟수</param>
    /// <returns>실제로 측정했으면 true.</returns>
    public bool TryMeasure(
        ulong targetId, long spotId, int shotIndex,
        float now, float spotTime, int rttMs,
        out int adjustedMs, out int fastCount)
    {
        adjustedMs = -1;
        fastCount = 0;

        if (spotId <= 0)
        {
            NoteFire(targetId, now);
            return false;
        }

        // --- 게이트 1 : 연사 도중 ---
        // 트리거를 이미 당기고 있었으므로 이 발사는 반응이 아니다.
        // shotIndex 0 만 통과시킨다.
        if (shotIndex > 0)
        {
            SkippedBurst++;
            NoteFire(targetId, now);
            return false;
        }

        // --- 게이트 2 : 같은 SPOT 재측정 ---
        if (_measured.TryGetValue(targetId, out long last) && last == spotId)
        {
            NoteFire(targetId, now);
            return false;
        }

        // --- 게이트 3 : 교전 중 재조우 ---
        // SPOT 직전까지 이 상대에게 쏘고 있었다면 첫 조우가 아니다.
        //
        // 기준을 now 가 아니라 spotTime 으로 잡는 것이 중요하다.
        // now 로 비교하면 연사 중 _lastFireAt 이 계속 갱신되어
        // 한 번 교전한 상대는 영원히 측정되지 않는다.
        //
        //   spotTime - lastFire = 시야가 끊겨 있던 시간
        //     짧으면(0.6초) 잠깐 가려졌다 나온 것 → 재조우
        //     길면(5초)     실제로 놓쳤다 다시 발견 → 첫 조우로 측정
        if (_lastFireAt.TryGetValue(targetId, out float lastFire) &&
            spotTime - lastFire < VTime.EngagementWindowSec)
        {
            SkippedReengage++;
            _measured[targetId] = spotId;
            NoteFire(targetId, now);
            return false;
        }

        _measured[targetId] = spotId;

        int raw = Mathf.RoundToInt((now - spotTime) * 1000f);

        // 업링크만 뺀다. 다운링크와 보간 지연은 되감은 기하에 이미 반영돼 있다.
        adjustedMs = raw - (rttMs > 0 ? rttMs / 2 : 0);

        _history.Enqueue(adjustedMs);
        while (_history.Count > VTime.WindowSize) _history.Dequeue();

        foreach (int v in _history)
            if (IsImpossible(v)) fastCount++;

        TotalMeasured++;
        NoteFire(targetId, now);
        return true;
    }

    /// <summary>
    /// 발사 시각을 기록한다. 측정했든 건너뛰었든 항상 호출해야
    /// 교전 상태가 끊기지 않는다.
    /// </summary>
    private void NoteFire(ulong targetId, float now) => _lastFireAt[targetId] = now;

    /// <summary>
    /// 측정 불확실성을 관대하게 잡고도 인간 하한을 밑도는가.
    /// 20Hz 양자화 때문에 측정치는 실제보다 최대 50ms 빠르다.
    /// </summary>
    public static bool IsImpossible(int adjustedMs)
        => adjustedMs + VTime.SpotQuantizationMs < VTime.ThresholdMs;

    /// <summary>표적이 사라지면 정리한다.</summary>
    public void Forget(ulong targetId)
    {
        _measured.Remove(targetId);
        _lastFireAt.Remove(targetId);
    }

    public void Reset()
    {
        _history.Clear();
        _measured.Clear();
        _lastFireAt.Clear();
        SkippedBurst = 0;
        SkippedReengage = 0;
    }
}