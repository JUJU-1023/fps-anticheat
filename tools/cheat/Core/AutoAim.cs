using System.Diagnostics;
using System.Numerics;
using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// 자동 조준의 "목표 공급자". AimLock 루프가 매 회(약 65Hz) TryGetAim 을 부르고,
/// true 면 그 각도를 쓰고 false 면 아무것도 쓰지 않는다(마우스가 그대로 조작).
///
/// 우클릭을 누르고 있는 동안만 동작한다. 항상 잠기면 시연 중 마우스를 못 쓴다.
/// 키 상태는 GetAsyncKeyState 로 읽기만 한다. 게임에 입력을 넣지 않는다.
///
/// ─ 반동 선보정 (W11 Day 2) ─
///   게임은 발사 프레임 안에서 GetRecoil(n) 을 currentYaw/Pitch 에 적용하고
///   같은 프레임에 입력을 만들어 보낸다. AimLock 은 그 사이에 끼어들 수 없어서,
///   서버가 받는 각도는 항상 반동 한 번분이 섞인다 (실측, match 128):
///       서버 yaw   = 조준 yaw   + h(n)
///       서버 pitch = 조준 pitch − v(n)      (pitch 양수 = 아래)
///   그래서 다음 발 n 을 위해 (yaw − h(n), pitch + v(n)) 을 미리 써 둔다.
///
///   n 은 시간으로 추정하지 않는다. 수평 반동이 발마다 ±0.35° 지그재그라 한 발만
///   어긋나도 0.6° 넘게 틀린다. 대신 "내가 쓴 값 − 다음 회차에 읽은 값" 이
///   GetRecoil(n) 과 일치하는지로 실제 발사를 감지해 n 을 센다.
///   어긋나면 0번(리셋)부터 가까운 인덱스 순으로 다시 맞춘다.
///
///   보정을 켜도 V-RECOIL 에는 그대로 걸린다. 서버가 보는 pitch 가 오히려
///   계산값으로 완전히 고정되기 때문이다.
/// </summary>
public sealed class AutoAim
{
    private readonly PlayerReader _reader;
    private readonly AimCheat _aim;

    /// <summary>조준선에서 이 각도(도) 안의 적만 잡는다.</summary>
    public volatile float MaxFovDeg = 30f;

    /// <summary>false = 몸통(기본, 히트박스가 커서 안정적) / true = 머리.</summary>
    public volatile bool AimHead = false;

    public volatile bool RequireRightMouse = true;

    /// <summary>반동 선보정. false 로 두면 보정 전과 비교할 수 있다.</summary>
    public volatile bool CompensateRecoil = true;

    /// <summary>진단 로그 출력 대상. null 이면 출력하지 않는다. 스레드 안전해야 한다(AimLock 스레드에서 호출).</summary>
    public Action<string>? Log;

    // ── 게임 WeaponConfig 와 같은 값 (WeaponConfig.cs 84~89행) ──
    private const int   RecoilRampShots     = 8;
    private const float RecoilVerticalStart = 0.4f;
    private const float RecoilVerticalMax   = 1.1f;
    private const float RecoilHorizontalMax = 0.35f;
    private const long  RecoilResetMs       = 350;    // RecoilResetTicks 21 × 16.67ms

    private const float MatchTolDeg  = 0.03f;   // 발사 감지 허용 오차
    private const float MinKickDeg   = 0.3f;    // 수직 반동 최소 0.4 → 이보다 작으면 발사 아님
    private const int   MaxResyncIdx = 40;

    // ── 반동 추적 (AimLock 스레드에서만 접근) ──
    private bool  _haveLast;            // 지난 회차에 각도를 돌려줬는가 (= AimLock 이 썼는가)
    private float _lastYaw, _lastPitch; // 그때 돌려준 각도 (보정 포함)
    private int   _lastCompIdx;         // 그때 보정에 쓴 shotIndex
    private int   _nextShot;            // 다음에 나갈 발의 shotIndex
    private long  _lastShotMs = long.MinValue / 2;

    // ── 진단 카운터 (AimLock 스레드에서만 접근) ──
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextReportMs = 1000;
    private int _calls, _noRmb, _noLocal, _noEnemy, _noProbe, _noPick, _ok;
    private int _shots, _resync, _unmatched;
    private string _detail = "";

    public AutoAim(PlayerReader reader, AimCheat aim)
    {
        _reader = reader;
        _aim = aim;
    }

    public bool TryGetAim(out float yaw, out float pitch)
    {
        bool r = TryGetAimCore(out yaw, out pitch);
        _haveLast = r;
        if (r) { _lastYaw = yaw; _lastPitch = pitch; }
        MaybeReport();
        return r;
    }

    private bool TryGetAimCore(out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        _calls++;

        if (RequireRightMouse && (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) == 0)
        {
            _noRmb++;
            return false;
        }

        if (!_reader.TryReadLocal(out var me)) { _noLocal++; return false; }

        var enemies = _reader.ReadLiveEnemies();
        if (enemies.Count == 0) { _noEnemy++; return false; }

        if (!_aim.Probe(out float curYaw, out float curPitch)) { _noProbe++; return false; }

        long now = _clock.ElapsedMilliseconds;
        if (_haveLast) DetectShot(curYaw, curPitch, now);

        // 진단용: 첫 번째 적에 대해 계산각과 현재각을 남긴다 (1:1 테스트 기준).
        _detail = Describe(me, enemies[0], curYaw, curPitch);

        if (!AimSolver.TryPickTarget(me, enemies, curYaw, curPitch, MaxFovDeg, AimHead, out yaw, out pitch))
        {
            _noPick++;
            return false;
        }

        if (CompensateRecoil)
        {
            // 350ms 넘게 안 쐈으면 게임이 반동 인덱스를 0 으로 되돌린다.
            int n = now - _lastShotMs > RecoilResetMs ? 0 : _nextShot;
            var (h, v) = GetRecoil(n);
            yaw = WrapYaw(yaw - h);
            pitch = Math.Clamp(pitch + v, -89f, 89f);
            _lastCompIdx = n;
        }

        _ok++;
        return true;
    }

    // -----------------------------------------------------------------
    //  반동
    // -----------------------------------------------------------------

    /// <summary>WeaponConfig.GetRecoil 과 같은 식. h = 수평, v = 수직 (deg).</summary>
    private static (float h, float v) GetRecoil(int shotIndex)
    {
        float ramp = Math.Clamp((float)shotIndex / RecoilRampShots, 0f, 1f);
        float v = RecoilVerticalStart + (RecoilVerticalMax - RecoilVerticalStart) * ramp;

        float hRamp = Math.Clamp(shotIndex / 5f, 0f, 1f);
        float h = MathF.Sin(shotIndex * 1.7f) * RecoilHorizontalMax * hRamp;

        return (h, v);
    }

    /// <summary>
    /// 지난 회차에 쓴 값과 지금 읽은 값의 차이가 반동 한 발과 일치하면 발사로 본다.
    ///   pitch: 반동이 위로 차므로 쓴 값 − 현재 = v(n)
    ///   yaw  : 현재 − 쓴 값 = h(n)
    /// </summary>
    private void DetectShot(float curYaw, float curPitch, long now)
    {
        float dp = _lastPitch - curPitch;
        float dy = AimSolver.DeltaAngle(curYaw, _lastYaw);
        if (dp < MinKickDeg) return;   // 발사 없음 (또는 마우스를 아래로)

        int matched = -1;
        if (Matches(_lastCompIdx, dp, dy))
        {
            matched = _lastCompIdx;
        }
        else
        {
            // 0(리셋)부터, 그다음 기대값에 가까운 순서로 다시 맞춘다.
            if (Matches(0, dp, dy)) matched = 0;
            for (int d = 1; matched < 0 && d <= MaxResyncIdx; d++)
            {
                if (Matches(_lastCompIdx + d, dp, dy)) matched = _lastCompIdx + d;
                else if (_lastCompIdx - d > 0 && Matches(_lastCompIdx - d, dp, dy)) matched = _lastCompIdx - d;
            }
            if (matched >= 0) _resync++;
        }

        if (matched < 0) { _unmatched++; return; }   // 마우스 움직임 등

        _shots++;
        _nextShot = matched + 1;
        _lastShotMs = now;
    }

    private static bool Matches(int idx, float dp, float dy)
    {
        var (h, v) = GetRecoil(idx);
        return MathF.Abs(dp - v) < MatchTolDeg && MathF.Abs(dy - h) < MatchTolDeg;
    }

    private static float WrapYaw(float y)
    {
        y %= 360f;
        return y < 0f ? y + 360f : y;
    }

    // -----------------------------------------------------------------
    //  진단
    // -----------------------------------------------------------------

    private string Describe(in StatePayloadRaw me, in EnemySnapshot e, float curYaw, float curPitch)
    {
        Vector3 eye = me.Position + new Vector3(0f, AimSolver.EyeOffsetY, 0f);
        float aimY = AimHead ? AimSolver.HeadOffsetY : AimSolver.BodyOffsetY;
        Vector3 point = e.State.Position + new Vector3(0f, aimY, 0f);
        var (y, p) = AimSolver.AnglesTo(eye, point);
        float dist = Vector3.Distance(eye, point);

        // 보정이 켜져 있으면 "현재"는 계산각에서 다음 발 반동만큼 벗어나 있는 게 정상이다.
        return $"나 tick {me.Tick} ({me.Position.X:F2},{me.Position.Y:F2},{me.Position.Z:F2}) · " +
               $"적 tick {e.State.Tick} ({e.State.Position.X:F2},{e.State.Position.Y:F2},{e.State.Position.Z:F2}) · " +
               $"거리 {dist:F1}m · 계산 {y:F2}/{p:F2} · 현재 {curYaw:F2}/{curPitch:F2} · " +
               $"차이 {AimSolver.DeltaAngle(y, curYaw):F2}/{p - curPitch:F2}";
    }

    private void MaybeReport()
    {
        long now = _clock.ElapsedMilliseconds;
        if (now < _nextReportMs) return;
        _nextReportMs = now + 1000;

        string readerDiag = _reader.TakeDiag();   // 안 찍어도 비워 둔다

        if (_calls > _noRmb && Log != null)
        {
            Log($"[자동조준] 호출 {_calls} · 우클릭X {_noRmb} · 내위치X {_noLocal} · 적없음 {_noEnemy} · " +
                $"Probe실패 {_noProbe} · FOV밖 {_noPick} · 성공 {_ok} | {readerDiag}");
            Log($"   반동보정 {(CompensateRecoil ? "ON" : "OFF")} · 발사감지 {_shots} · 재동기 {_resync} · " +
                $"불일치 {_unmatched} · 다음발 {_nextShot}");
            if (_detail.Length > 0) Log("   " + _detail);
        }

        _calls = _noRmb = _noLocal = _noEnemy = _noProbe = _noPick = _ok = 0;
        _shots = _resync = _unmatched = 0;
        _detail = "";
    }
}
