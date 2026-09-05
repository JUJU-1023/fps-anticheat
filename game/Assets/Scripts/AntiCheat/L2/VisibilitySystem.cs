// =====================================================================
//  VisibilitySystem.cs
//  경로: game/Assets/Scripts/AntiCheat/L2/VisibilitySystem.cs
//
//  20Hz 가시성 루프. 서버에서만 돌고 인스턴스는 하나다.
//
//  왜 하나로 묶는가
//   V-LOS-01(차폐 추적)과 V-TIME-01(SPOT)은 둘 다 "A가 B를 볼 수 있는가"를
//   묻는다. 검증기마다 Raycast를 따로 쏘면 같은 계산을 두 번 한다.
//
//  왜 20Hz 인가
//   반응시간 해상도 50ms 면 트리거봇(0ms 근처)과 사람(150ms 이상)을
//   구분하기에 충분하다. 다만 이 50ms 는 측정 편향이 되므로
//   ReactionTimeValidator 가 판정에서 보정한다.
//
//  ─────────────────────────────────────────────────────────────────
//  V-LOS-01 을 왜 "벽 너머 피격"이 아니라 "차폐 추적"으로 보는가
//
//   원안은 벽 너머로 맞은 히트를 잡는 것이었다. 그런데 FireHitscan 은
//   되감은 월드에서 벽이 더 가까우면 히트를 성립시키지 않는다.
//   벽 너머 피격은 구조적으로 불가능하고, 잡을 대상이 없다.
//   (마스크 오설정에 대비해 WeaponSystem 에 BlockedHit 을 남겨둔다.)
//
//   사람은 벽 뒤 적의 위치를 모르므로 우연히 겨눌 수는 있어도
//   따라다니지 못한다. 월핵은 따라다닌다.
//   V-MOVE-01 에서 위치 델타 검사를 입력 수신율로 바꾼 것과 같은 판단이다.
//
//  ─────────────────────────────────────────────────────────────────
//  시점에 관하여
//
//   관찰자 A 의 눈  : 현재 위치. 클라 예측이 있어 A 는 자기를 현재로 본다.
//   대상 B 의 중심  : now - (RTT_A/2 + 보간지연) 시점의 되감은 위치.
//                     A 화면에 실제로 그려져 있는 위치다.
//   FireHitscan 과 같은 공식이라 두 데이터를 함께 해석할 수 있다.
//   덕분에 SPOT 시각이 곧 "A 화면에 뜬 순간"이 되고,
//   V-TIME 정규화는 업링크(RTT/2)만 빼면 된다.
//
//  판정할 수 없으면 무죄
//   TryBodyCenterAt 이 false 면 그 쌍은 이번 프레임을 건너뛴다.
//
//  ─────────────────────────────────────────────────────────────────
//  W7 Day 3 : V-LOS-01 위반 발화
//  W7 Day 4 : SPOT 텔레메트리 방출  ← 이번 변경
//
//   SPOT 을 combat_events 에 event_type='SPOT' 으로 남긴다.
//   위반만 기록하면 정상 플레이어의 반응시간 분포가 없어서
//   Day 5 에서 임계값을 실측으로 정할 수 없고, W13 feature 도 못 만든다.
//   반응시간은 SQL 에서 유도한다.
//     fire.server_time - spot.server_time - fire.rtt_ms/2
//   전환 시에만 발생하므로 볼륨은 작다.
// =====================================================================

using System.Collections.Generic;
using System.Text;
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
    /// 실제 카메라 FOV 와 맞춰야 한다.
    /// </summary>
    public const float FovHalfDeg = 55f;

    /// <summary>
    /// 이 각도 안에서 차폐된 적을 겨누고 있으면 "추적"으로 센다.
    /// 잠정값. Day 5 실측 후 확정.
    /// </summary>
    public const float TrackConeDeg = 5f;

    /// <summary>벽 표면 근접 허용 오차. 모서리 스치기 오탐을 줄인다.</summary>
    public const float OccludeMargin = 0.05f;

    /// <summary>시야에서 벗어난 뒤 SPOT 을 해제하기까지의 유예.</summary>
    public const float ForgetSec = 0.5f;

    /// <summary>추적이 끊겼을 때 누적을 깎는 배율. 일시적 이탈을 흡수한다.</summary>
    public const float TrackDecayMul = 2f;

    /// <summary>누적 상한.</summary>
    public const float TrackCapSec = 5f;

    /// <summary>
    /// 위반으로 기록할 누적 임계(초). ★ 잠정값 ★
    /// 사람도 벽 뒤 적의 마지막 위치를 몇 초 겨누고 기다릴 수 있으므로
    /// Day 5 정상 플레이 실측 없이 이 숫자를 신뢰하면 안 된다.
    /// </summary>
    public const float TrackViolationSec = 2.0f;

    /// <summary>이 값 아래로 내려가야 같은 쌍을 다시 보고한다(히스테리시스).</summary>
    public const float TrackClearSec = 0.5f;

    public const string CODE = "V-LOS-01";

    /// <summary>월핵은 이동 위반보다 심각도가 높다.</summary>
    public const int Severity = 3;
}


public class VisibilitySystem : MonoBehaviour
{
    public static VisibilitySystem Instance { get; private set; }

    private class Entry
    {
        public ulong clientId;
        public PlayerController ctrl;
        public PlayerRewind rewind;
        public PlayerHealth health;
        public PlayerTelemetry telemetry;

        public string Uid => telemetry != null ? telemetry.PlayerUid : "unknown";
    }

    private struct Pair
    {
        public bool visible;
        public float lostAt;            // 비가시 전환 시각 (0 = 해당 없음)
        public float spotTime;          // 마지막 SPOT 성립 시각
        public long spotId;
        public float occludedTrackSec;  // 차폐 추적 누적
        public float peakTrackSec;      // 세션 내 최대치 (튜닝용)
        public bool reported;          // 이번 에피소드에서 이미 보고했는가
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<(ulong, ulong), Pair> _pairs = new();
    private readonly List<(ulong, ulong)> _removeKeys = new();

    private static readonly StringBuilder _sb = new StringBuilder(320);

    private long _nextSpotId = 1;
    private int _tickCounter;
    private int _worldMask;

    // --- 통계 (5초마다 출력) ---
    private int _rayCount;
    private int _noSampleCount;
    private int _spotCount;
    private int _violationCount;
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
        var vs = go.AddComponent<VisibilitySystem>();
        if (Instance == null) Instance = vs;
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
            clientId = ctrl.OwnerClientId,
            ctrl = ctrl,
            rewind = ctrl.GetComponent<PlayerRewind>(),
            health = ctrl.GetComponent<PlayerHealth>(),
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
        int tick = NetworkTickSystem.Instance != null
                   ? NetworkTickSystem.Instance.CurrentTick : 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            var a = _entries[i];
            if (a.ctrl == null) continue;
            if (a.health != null && a.health.IsDead) continue;

            Vector3 eye = a.ctrl.ServerEyePosition;
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
                    _noSampleCount++;      // 이력이 닿지 않는다. 판정 보류.
                    continue;
                }

                Vector3 to = center - eye;
                float dist = to.magnitude;
                if (dist < 0.01f) continue;

                _pairs.TryGetValue(key, out Pair st);

                // --- 거리 컬링 ---
                if (dist > VLos.MaxTrackDist)
                {
                    UpdateVisibility(ref st, false, now);
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                    ClearReportFlag(ref st);
                    _pairs[key] = st;
                    continue;
                }

                Vector3 dir = to / dist;
                float angle = Vector3.Angle(aimDir, dir);

                // --- 시야각 컬링 (Raycast 이전에 한다) ---
                if (angle > VLos.FovHalfDeg)
                {
                    UpdateVisibility(ref st, false, now);
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                    ClearReportFlag(ref st);
                    _pairs[key] = st;
                    continue;
                }

                // --- 차폐 판정 ---
                bool blocked = Physics.Raycast(
                    eye, dir, dist - VLos.OccludeMargin,
                    _worldMask, QueryTriggerInteraction.Ignore);
                _rayCount++;

                // --- SPOT (V-TIME-01) ---
                if (UpdateVisibility(ref st, !blocked, now))
                    EmitSpot(a, b, st.spotId, tick, dist, angle, rtt,
                             Mathf.RoundToInt(rewindSec * 1000f));

                // --- 차폐 추적 누적 (V-LOS-01) ---
                if (blocked && angle < VLos.TrackConeDeg)
                {
                    st.occludedTrackSec =
                        Mathf.Min(VLos.TrackCapSec, st.occludedTrackSec + dt);

                    if (st.occludedTrackSec > st.peakTrackSec)
                        st.peakTrackSec = st.occludedTrackSec;
                    if (st.occludedTrackSec > _globalPeakTrack)
                        _globalPeakTrack = st.occludedTrackSec;

                    // 에피소드당 한 번만 보고한다.
                    if (!st.reported && st.occludedTrackSec >= VLos.TrackViolationSec)
                    {
                        st.reported = true;
                        _violationCount++;
                        ViolationLogger.Report(
                            clientId: a.clientId,
                            playerUid: a.Uid,
                            code: VLos.CODE,
                            tick: tick,
                            severity: VLos.Severity,
                            detail: "OccludedTracking",
                            rttMs: rtt);
                    }
                }
                else
                {
                    st.occludedTrackSec = Decay(st.occludedTrackSec, dt);
                    ClearReportFlag(ref st);
                }

                _pairs[key] = st;
            }
        }

        EmitStats(now);
    }

    private static float Decay(float v, float dt)
        => Mathf.Max(0f, v - dt * VLos.TrackDecayMul);

    /// <summary>누적이 충분히 내려가면 다음 에피소드를 보고할 수 있게 푼다.</summary>
    private static void ClearReportFlag(ref Pair st)
    {
        if (st.reported && st.occludedTrackSec <= VLos.TrackClearSec)
            st.reported = false;
    }

    /// <summary>
    /// 가시 상태를 갱신하고 새 SPOT 이면 id 를 발급한다.
    /// 비가시로 바뀔 때는 ForgetSec 만큼 버틴다. 얇은 기둥 뒤를
    /// 스쳐 지나가는 적에게 SPOT 이 연속 발생하는 것을 막는다.
    /// </summary>
    /// <returns>이번 호출에서 새 SPOT 이 성립했으면 true</returns>
    private bool UpdateVisibility(ref Pair st, bool visible, float now)
    {
        if (visible)
        {
            bool isNew = !st.visible;
            if (isNew)
            {
                st.visible = true;
                st.spotTime = now;
                st.spotId = _nextSpotId++;
                _spotCount++;
            }
            st.lostAt = 0f;
            return isNew;
        }

        if (st.visible)
        {
            if (st.lostAt <= 0f) st.lostAt = now;
            else if (now - st.lostAt >= VLos.ForgetSec)
            {
                st.visible = false;
                st.lostAt = 0f;
            }
        }
        return false;
    }

    private void ResetPair((ulong, ulong) key)
    {
        if (!_pairs.TryGetValue(key, out var st)) return;
        st.visible = false;
        st.lostAt = 0f;
        st.occludedTrackSec = 0f;
        st.reported = false;
        _pairs[key] = st;
    }

    // -----------------------------------------------------------------
    //  SPOT 텔레메트리
    // -----------------------------------------------------------------

    /// <summary>
    /// SPOT 한 건을 combat_events 로 내보낸다.
    /// 반응시간은 이 행과 뒤따르는 FIRE 행을 spot_event_id 로 이어
    /// SQL 에서 유도한다. 서버가 미리 계산해 두지 않는 이유는
    /// 정상 플레이어의 분포 자체가 Day 5 임계 결정과 W13 feature 의
    /// 원재료이기 때문이다.
    /// </summary>
    private void EmitSpot(Entry a, Entry b, long spotId, int serverTick,
                          float dist, float angleDeg, int rttMs, int rewindMs)
    {
        var w = TelemetryWriter.Instance;
        if (w == null || !w.IsActive) return;

        _sb.Clear();
        _sb.Append("{\"t\":\"combat\"");
        _sb.Append(",\"match_uid\":").Append(TJson.Str(w.MatchUid));
        _sb.Append(",\"player_uid\":").Append(TJson.Str(a.Uid));
        _sb.Append(",\"target_uid\":").Append(TJson.Str(b.Uid));
        _sb.Append(",\"event_type\":\"SPOT\"");
        _sb.Append(",\"spot_event_id\":").Append(spotId.ToString(TJson.Inv));
        _sb.Append(",\"server_tick\":").Append(serverTick.ToString(TJson.Inv));
        _sb.Append(",\"ts\":").Append(TJson.Str(TJson.Now()));
        _sb.Append(",\"target_dist\":").Append(TJson.F(dist));
        _sb.Append(",\"aim_error_deg\":").Append(TJson.F(angleDeg));
        _sb.Append(",\"rewind_ms\":").Append(rewindMs.ToString(TJson.Inv));
        _sb.Append(",\"is_headshot\":false");
        _sb.Append(",\"rtt_ms\":").Append(rttMs >= 0 ? rttMs.ToString(TJson.Inv) : "null");
        _sb.Append('}');

        w.Write(_sb.ToString());
    }

    private void EmitStats(float now)
    {
        if (now - _lastStatAt < 5f) return;
        float span = now - _lastStatAt;
        _lastStatAt = now;

        Debug.Log($"[LOS] players={_entries.Count} pairs={_pairs.Count} " +
                  $"rays/s={_rayCount / span:F0} spots={_spotCount} " +
                  $"noSample={_noSampleCount} viol={_violationCount} " +
                  $"peakTrack={_globalPeakTrack:F2}s");

        _rayCount = 0;
        _noSampleCount = 0;
        _spotCount = 0;
        _violationCount = 0;
    }

    // -----------------------------------------------------------------
    //  조회 API
    // -----------------------------------------------------------------

    /// <summary>A 가 B 를 보고 있으면 SPOT 시각과 id 를 돌려준다.</summary>
    public bool TryGetSpot(ulong observer, ulong target, out float spotTime, out long spotId)
    {
        spotTime = 0f; spotId = 0;
        if (!_pairs.TryGetValue((observer, target), out var st) || !st.visible) return false;
        spotTime = st.spotTime;
        spotId = st.spotId;
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