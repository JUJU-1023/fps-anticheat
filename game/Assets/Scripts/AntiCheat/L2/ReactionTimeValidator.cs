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
//   ※ 현재 서버 위치로 판정했다면 raw - RTT - 보간지연 이어야 한다.
//     값은 같고 어느 기하를 쓰느냐의 문제다. 둘을 섞으면 틀린다.
//
//  ─────────────────────────────────────────────────────────────────
//  20Hz 양자화 보정  ★
//
//   가시성 루프가 20Hz 라 실제 시야 확보보다 최대 50ms 늦게 SPOT 을
//   잡는다. spotTime 이 늦어지면 반응시간은 그만큼 짧게 측정된다.
//   즉 측정값은 실제보다 평균 25ms, 최대 50ms 빠르다.
//
//   임계 110ms 에 대해 이 편향은 무시할 수 없다. 그대로 두면
//   정상 플레이어가 빨라 보여 오탐이 늘어난다.
//   그래서 위반 판정에 불확실성을 더한다.
//
//     adjusted + SpotQuantizationMs < ThresholdMs
//
//   "가장 관대하게 해석해도 인간이 불가능한 속도일 때만" 잡는다.
//   V-LOS 에서 이력이 닿지 않으면 판정을 보류한 것과 같은 원칙이다.
//   완벽한 트리거봇은 adjusted 가 0 근처라 여전히 걸린다.
//
//  ─────────────────────────────────────────────────────────────────
//  왜 차단하지 않는가
//
//   확률적 지표다. 코너를 미리 겨누고 있다가 적이 나타나면 사람도
//   0ms 에 가까운 반응이 나온다. 정당한 플레이다.
//   그래서 단발은 severity 1 로 기록만 하고, 최근 20회 중 5회 이상
//   반복될 때 severity 3 으로 올린다. 차단은 하지 않는다.
//   W13 Trust Score 의 입력으로 쓴다.
//
//  ─────────────────────────────────────────────────────────────────
//  연사 처리
//
//   한 번 발사를 시작하면 초당 10발이 나간다. 매 발마다 같은 SPOT 을
//   기준으로 재면 두 번째 발부터는 점점 커지는 무의미한 값이 쌓여
//   분포가 오염된다. SPOT 하나당 첫 발만 잰다.
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

    /// <summary>표적별로 마지막에 측정한 SPOT id. 연사 중복 측정을 막는다.</summary>
    private readonly Dictionary<ulong, long> _measured = new();

    public int TotalMeasured { get; private set; }

    /// <summary>
    /// 이번 발사의 반응시간을 잰다. 같은 SPOT 을 두 번 재지 않는다.
    /// </summary>
    /// <param name="targetId">표적 clientId</param>
    /// <param name="spotId">VisibilitySystem 이 발급한 SPOT id</param>
    /// <param name="now">Time.realtimeSinceStartup (발사 입력이 서버에 도착한 시각)</param>
    /// <param name="spotTime">SPOT 성립 시각</param>
    /// <param name="rttMs">서버 측정 RTT</param>
    /// <param name="adjustedMs">RTT 정규화한 반응시간</param>
    /// <param name="fastCount">최근 창에서 임계 미만이었던 횟수</param>
    /// <returns>이번 SPOT 을 처음 측정했으면 true. 연사 후속탄이면 false.</returns>
    public bool TryMeasure(
        ulong targetId, long spotId,
        float now, float spotTime, int rttMs,
        out int adjustedMs, out int fastCount)
    {
        adjustedMs = -1;
        fastCount  = 0;

        if (spotId <= 0) return false;

        if (_measured.TryGetValue(targetId, out long last) && last == spotId)
            return false;                      // 같은 교전의 후속탄
        _measured[targetId] = spotId;

        int raw = Mathf.RoundToInt((now - spotTime) * 1000f);

        // 업링크만 뺀다. 다운링크와 보간 지연은 되감은 기하에 이미 반영돼 있다.
        adjustedMs = raw - (rttMs > 0 ? rttMs / 2 : 0);

        _history.Enqueue(adjustedMs);
        while (_history.Count > VTime.WindowSize) _history.Dequeue();

        foreach (int v in _history)
            if (IsImpossible(v)) fastCount++;

        TotalMeasured++;
        return true;
    }

    /// <summary>
    /// 측정 불확실성을 관대하게 잡고도 인간 하한을 밑도는가.
    /// 20Hz 양자화 때문에 측정치는 실제보다 최대 50ms 빠르다.
    /// </summary>
    public static bool IsImpossible(int adjustedMs)
        => adjustedMs + VTime.SpotQuantizationMs < VTime.ThresholdMs;

    /// <summary>표적이 사라지면 정리한다.</summary>
    public void Forget(ulong targetId) => _measured.Remove(targetId);

    public void Reset()
    {
        _history.Clear();
        _measured.Clear();
    }
}
