// =====================================================================
//  VisibilitySystem.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/VisibilitySystem.cs
//
//  20Hz 가시성 루프. 서버에서만 돌고 인스턴스는 하나다.
//
//  왜 하나로 묶는가
//   V-LOS-01(차폐 추적)과 V-TIME-01(SPOT)은 둘 다 "A가 B를 볼 수 있는가"를
//   묻는다. 검증기마다 Raycast를 따로 쏘면 같은 계산을 두 번 한다.
//   한 번의 차폐 판정에서 두 신호를 모두 뽑는다.
//
//  왜 20Hz 인가
//   반응시간 해상도 50ms면 충분하다. 인간 반응 하한이 150ms 수준이라
//   50ms 격자로도 트리거봇(0ms에 가까움)과 구분된다.
//   60Hz로 돌리면 Raycast 비용만 3배가 되고 얻는 게 없다.
//
//  ─────────────────────────────────────────────────────────────────
//  V-LOS-01 을 왜 "벽 너머 피격"이 아니라 "차폐 추적"으로 보는가
//
//   W7 계획 문서의 원안은 벽 너머로 맞은 히트를 잡는 것이었다.
//   그런데 이 프로젝트의 FireHitscan 은 되감은 월드에서 레이를 쏘고
//   벽이 더 가까우면 PlayerRewind 가 null 이라 히트가 성립하지 않는다.
//   벽 너머 피격은 이미 구조적으로 불가능하고, 잡을 대상이 없다.
//
//   실제 월핵의 관측 가능한 신호는 다른 데 있다.
//   사람은 벽 뒤 적의 위치를 모르므로 우연히 겨눌 수는 있어도
//   따라다니지 못한다. 월핵은 따라다닌다.
//   → "차폐된 적을 좁은 각도 안에서 지속 추적한 시간"을 누적한다.
//
//   V-MOVE-01 에서 위치 델타 검사가 발화하지 않아 입력 수신율로
//   바꾼 것과 같은 종류의 판단이다. 서버 권위 구조가 원래의 공격 경로를
//   막아버리면, 남은 신호를 다시 찾아야 한다.
//
//  ─────────────────────────────────────────────────────────────────
//  시점에 관하여 (중요)
//
//   관찰자 A 의 눈    : 현재 위치.
//                       클라이언트 예측이 있으므로 A 는 자기를 현재로 본다.
//   대상 B 의 중심    : now - (RTT_A/2 + 보간지연) 시점의 되감은 위치.
//                       A 화면에 실제로 그려져 있는 위치가 그것이다.
//
//   FireHitscan 의 되감기 공식과 동일하다. 히트 판정과 가시성 판정이
//   다른 시점을 보면 두 데이터를 함께 해석할 수 없다.
//
//   부수 효과로 SPOT 시각이 "A 화면에 뜬 순간"이 되므로,
//   V-TIME-01 의 반응시간 정규화는 업링크만 빼면 된다 (raw - RTT/2).
//   현재 위치로 판정했다면 raw - RTT - 보간지연 이어야 한다. 값은 같다.
//
//  ─────────────────────────────────────────────────────────────────
//  판정할 수 없으면 무죄
//
//   TryBodyCenterAt 이 false 를 돌려주면 (이력 버퍼가 그 시각까지
//   닿지 않으면) 그 쌍은 이번 프레임을 통째로 건너뛴다.
//   렉 구간에서 정상 플레이어를 잡는 것이 놓치는 것보다 나쁘다.
// =====================================================================

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public static class VLos
{
    /// <summary>몇 서버 틱마다 한 번 도는가. 60Hz / 3 = 20Hz.</summary>
    public const int TickDivisor = 3;

    /// <summary>이 거리를 넘으면 판정 대상에서 제외한다.</summary>
    public const float MaxTrackDist = 100f;

    /// <summary>
    /// 시야각 절반. 화면 밖의 적은 SPOT 이 성립하지 않는다.
    /// 실제 카메라 FOV 와 맞춰야 한다. 좁게 잡으면 SPOT 을 놓치고
    /// 넓게 잡으면 화면에 없던 적에 반응한 것으로 오인한다.
    /// </summary>
    public const float FovHalfDeg = 55f;

    /// <summary>
    /// 이 각도 안에서 차폐된 적을 겨누고 있으면 "추적"으로 센다.
    /// W7 Day 5 실측 분포를 보고 확정한다. 지금 값은 추정치다.
    /// </summary>
    public const float TrackConeDeg = 5f;

    /// <summary>벽 표면 근접 허용 오차. 모서리 스치기 오탐을 줄인다.</summary>
    public const float OccludeMargin = 0.05f;

    /// <summary>
    /// 시야에서 벗어난 뒤 SPOT 을 해제하기까지의 유예.
    /// 얇은 기둥 뒤를 지나가는 적에게 SPOT 이 연속 발생하는 것을 막는다.
    /// </summary>
    public const float ForgetSec = 0.5f;

    /// <summary>추적이 끊겼을 때 누적을 깎는 배율. 사람의 일시적 이탈을 흡수한다.</summary>
    public const float TrackDecayMul = 2f;

    /// <summary>누적 상한. 무한히 쌓여 복구 불가능해지는 것을 막는다.</summary>
    public const float TrackCapSec = 5f;

    public const string CODE = "V-LOS-01";
}


public class VisibilitySystem : MonoBehaviour
{
    public static VisibilitySystem Instance { get; private set; }

    private class Entry
    {
        public ulong            clientId;
        public PlayerController ctrl;
        public PlayerRewind     rewind;
        public PlayerHealth     health;
        public PlayerTelemetry  telemetry;
    }

    private struct Pair
    {
        public bool  visible;
        public float lostAt;            // 비가시 전환 시각 (0 = 해당 없음)
        public float spotTime;          // 마지막 SPOT 성립 시각
        public long  spotId;
        public float occludedTrackSec;  // 차폐 추적 누적
        public float peakTrackSec;      // 세션 내 최대치 (튜닝용)
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<(ulong, ulong), Pair> _pairs = new();
    private readonly List<(ulong, ulong)> _removeKeys = new();

    private long _nextSpotId = 1;
    private int  _tickCounter;
    private int  _worldMask;

    // --- 통계 (5초마다 출력) ---
    private int   _rayCount;
    private int   _noSampleCount;
    private int   _spotCount;
    private float _globalPeakTrack;
    private float _lastStatAt;

    // -----------------------------------------------------------------
    //  수명
    // -----------------------------------------------------------------

    /// <summary>서버에서 첫 플레이어가 스폰될 때 호출된다.</summary>
    public static void EnsureExists()
    {
        if (Instance != null) return;
        var go = new GameObject("VisibilitySystem");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VisibilitySystem>();
    }

    private void Awake()
    {
        Instance = this;

        // 벽만 본다. 플레이어 본체(Player)와 히트박스(Hitbox)는 제외한다.
        // 히트박스가 섞이면 다른 플레이어가 시야를 가린 것을 벽으로 오인한다.
        int world = LayerMask.NameToLayer("Default");
        if (world < 0)
        {
            Debug.LogError("[LOS] 'Default' 레이어를 찾을 수 없다.");
            _worldMask = 0;
        }
        else _worldMask = 1 << world;

        _lastStatAt = Time.realtimeSinceStartup;
    }

    public void Register(PlayerController ctrl)
    {
        if (ctrl == null) return;
        foreach (var e in _entries) if (e.ctrl == ctrl) return;

        _entries.Add(new Entry
        {
            clientId  = ctrl.OwnerClientId,
            ctrl      = ctrl,
            rewind    = ctrl.GetComponent<PlayerRewind>(),
            health    = ctrl.GetComponent<PlayerHealth>(),
            telemetry = ctrl.GetComponent<PlayerTelemetry>(),
        });

        Debug.Log($"[LOS] 등록 client={ctrl.OwnerClientId} count={_entries.Count}");
    }

    public void Unregister(PlayerController ctrl)
    {
        if (ctrl == null) return;

        ulong id = ctrl.OwnerClientId;
        _entries.RemoveAll(e => e.ctrl == ctrl);

        _removeKeys.Clear();
        foreach (var kv in _pairs)
            if (kv.Key.Item1 == id || kv.Key.Item2 == id) _removeKeys.Add(kv.Key);
        foreach (var k in _removeKeys) _pairs.Remove(k);
    }

    // -----------------------------------------------------------------
    //  루프
    // -----------------------------------------------------------------

    private void FixedUpdate()
    {
        if (_entries.Count < 2) { _tickCounter = 0; return; }

        if (++_tickCounter < VLos.TickDivisor) return;
        float dt = _tickCounter * NetworkTickSystem.TickInterval;
        _tickCounter = 0;

        float now = Time.realtimeSinceStartup;

        for (int i = 0; i < _entries.Count; i++)
        {
            var a = _entries[i];
            if (a.ctrl == null) continue;
            if (a.health != null && a.health.IsDead) continue;

            Vector3 eye    = a.ctrl.ServerEyePosition;
            Vector3 aimDir = a.ctrl.ServerAimDirection;

            // A 화면에 그려져 있는 시점. FireHitscan 과 같은 공식이다.
            int rtt = a.ctrl.ServerRttMs();
            float rewindSec = Mathf.Clamp(
                (rtt > 0 ? rtt / 2000f : 0f) + WeaponConfig.InterpolationDelaySec,
                0f, WeaponConfig.MaxRewindSec);
            float tRewind = now - rewindSec;

            for (int j = 0; j < _entries.Count; j++)
            {
                if (i == j) continue;
                var b = _entries[j];
                if (b.ctrl == null || b.rewind == null) continue;

                var key = (a.clientId, b.clientId);

                if (b.health != null && b.health.IsDead)
                {
                    ResetPair(key);
                    continue;
                }

                // --- 되감은 몸통 중심 ---
                if (!b.rewind.TryBodyCenterAt(tRewind, out Vector3 center))
                {
                    // 이력이 닿지 않는다. 판정 보류.
                    _noSampleCount++;
                    continue;
                }

                Vector3 to   = center - eye;
                float   dist = to.magnitude;
                if (dist < 0.01f) continue;

                _pairs.TryGetValue(key, out Pair st);

                // --- 거리 컬링 ---
                if (dist > VLos.MaxTrackDist)
                {
                    UpdateVisibility(ref st, false, now);
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                    _pairs[key] = st;
                    continue;
                }

                Vector3 dir   = to / dist;
                float   angle = Vector3.Angle(aimDir, dir);

                // --- 시야각 컬링 (Raycast 이전에 한다) ---
                if (angle > VLos.FovHalfDeg)
                {
                    UpdateVisibility(ref st, false, now);
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                    _pairs[key] = st;
                    continue;
                }

                // --- 차폐 판정 ---
                bool blocked = Physics.Raycast(
                    eye, dir, dist - VLos.OccludeMargin,
                    _worldMask, QueryTriggerInteraction.Ignore);
                _rayCount++;

                UpdateVisibility(ref st, !blocked, now);

                // --- 차폐 추적 누적 (V-LOS-01) ---
                if (blocked && angle < VLos.TrackConeDeg)
                {
                    st.occludedTrackSec =
                        Mathf.Min(VLos.TrackCapSec, st.occludedTrackSec + dt);
                    if (st.occludedTrackSec > st.peakTrackSec)
                        st.peakTrackSec = st.occludedTrackSec;
                    if (st.occludedTrackSec > _globalPeakTrack)
                        _globalPeakTrack = st.occludedTrackSec;
                }
                else
                {
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                }

                _pairs[key] = st;
            }
        }

        EmitStats(now);
    }

    private static float Decay(float v, float dt)
        => Mathf.Max(0f, v - dt * VLos.TrackDecayMul);

    /// <summary>
    /// 가시 상태를 갱신하고 새 SPOT 이면 id 를 발급한다.
    /// 비가시로 바뀔 때는 ForgetSec 만큼 버틴다. 얇은 기둥 뒤를
    /// 스쳐 지나가는 적에게 SPOT 이 연속 발생하는 것을 막는다.
    /// </summary>
    private void UpdateVisibility(ref Pair st, bool visible, float now)
    {
        if (visible)
        {
            if (!st.visible)
            {
                st.visible  = true;
                st.spotTime = now;
                st.spotId   = _nextSpotId++;
                _spotCount++;
            }
            st.lostAt = 0f;
        }
        else if (st.visible)
        {
            if (st.lostAt <= 0f) st.lostAt = now;
            else if (now - st.lostAt >= VLos.ForgetSec)
            {
                st.visible = false;
                st.lostAt  = 0f;
            }
        }
    }

    private void ResetPair((ulong, ulong) key)
    {
        if (!_pairs.TryGetValue(key, out var st)) return;
        st.visible          = false;
        st.lostAt           = 0f;
        st.occludedTrackSec = 0f;
        _pairs[key] = st;
    }

    private void EmitStats(float now)
    {
        if (now - _lastStatAt < 5f) return;
        float span = now - _lastStatAt;
        _lastStatAt = now;

        Debug.Log($"[LOS] players={_entries.Count} pairs={_pairs.Count} " +
                  $"rays/s={_rayCount / span:F0} spots={_spotCount} " +
                  $"noSample={_noSampleCount} peakTrack={_globalPeakTrack:F2}s");

        _rayCount = 0;
        _noSampleCount = 0;
        _spotCount = 0;
    }

    // -----------------------------------------------------------------
    //  조회 API (Day 3 / Day 4 에서 쓴다)
    // -----------------------------------------------------------------

    /// <summary>A 가 B 를 보고 있으면 SPOT 시각과 id 를 돌려준다.</summary>
    public bool TryGetSpot(ulong observer, ulong target, out float spotTime, out long spotId)
    {
        spotTime = 0f; spotId = 0;
        if (!_pairs.TryGetValue((observer, target), out var st) || !st.visible) return false;
        spotTime = st.spotTime;
        spotId   = st.spotId;
        return true;
    }

    /// <summary>A 가 차폐된 B 를 좁은 각도로 추적한 누적 시간(초).</summary>
    public float GetOccludedTrackSec(ulong observer, ulong target)
        => _pairs.TryGetValue((observer, target), out var st) ? st.occludedTrackSec : 0f;

    /// <summary>A 가 이번 세션에서 기록한 최대 차폐 추적 시간. 임계 튜닝용.</summary>
    public float GetPeakTrackSec(ulong observer, ulong target)
        => _pairs.TryGetValue((observer, target), out var st) ? st.peakTrackSec : 0f;

    /// <summary>서버 종료 시 정리.</summary>
    public void Clear()
    {
        _entries.Clear();
        _pairs.Clear();
    }
}
