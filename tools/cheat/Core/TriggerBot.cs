using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// 트리거봇 (W12). 조준선이 적 히트박스 위에 있으면 자동으로 쏜다.
///
/// ─ 판정 ─
///   눈 위치(내 루트 + 0.75)에서 현재 조준각 방향으로 광선을 쏴, 적의
///     머리: 구 (중심 루트 + 0.75, 반지름 HeadRadius)
///     몸통: 세로 캡슐 (루트 + BodyBottomY ~ 루트 + BodyTopY, 반지름 BodyRadius)
///   를 지나는지 본다. 적 위치는 자동 조준과 같은 latestTick−6 슬롯
///   (화면에 보이는 시점 = 서버 랙 보상 시점)이다.
///   히트박스는 서버보다 조금 작게 잡는다. 가장자리에서 쏘면 빗나가기 때문이다.
///
///   ※ 시야(벽) 판정은 없다. 외부 프로세스는 맵 충돌체를 레이캐스트할 수 없다.
///     벽 너머 적에 조준선이 겹치면 벽에 대고 쏜다.
///
/// ─ 발사 ─
///   조준선이 적 위에 있는 동안 왼쪽 버튼을 누르고 있고(SendInput), 벗어나면 뗀다.
///   OS 입력 큐에 넣는 것이라 게임 프로세스에는 아무것도 주입하지 않는다.
///   게임 창이 맨 앞이 아니면 절대 누르지 않는다(다른 창을 클릭하게 됨).
///
/// ─ 반응 지연 ─
///   ReactionDelayMs = 0 이면 조준선이 닿는 즉시 쏜다 → V-TIME(60ms) 시연용.
///   값을 주면 그만큼 계속 겹쳐 있어야 쏜다 → "사람처럼 늦게 쏴서 V-TIME 을 피하면
///   L3 가 잡는가" 실험용 (Day 3 의 S3 와 같은 구조).
///
/// ─ 탐지 예상 ─
///   V-TIME-01: SPOT → 발사가 기계적으로 짧다. 교전 창(3초) 게이트에 일부 가려짐.
///   L3: 반응시간 분포가 좁고 일정하다(지연을 줘도 "일정함"은 남는다).
/// </summary>
public sealed class TriggerBot : IDisposable
{
    // 히트박스 근사 (적 루트 위치 기준, 서 있으면 루트 y = 바닥 + 1.08).
    // 서버: Head 중심 1.83, Body 중심 0.88 (바닥 기준). 반지름은 서버보다 작게.
    private const float HeadOffsetY = 0.75f;    // = AimSolver.HeadOffsetY
    private const float HeadRadius  = 0.11f;
    private const float BodyBottomY = -0.75f;   // 바닥 + 0.33
    private const float BodyTopY    =  0.35f;   // 바닥 + 1.43
    private const float BodyRadius  = 0.20f;
    private const float MaxRangeM   = 150f;

    private readonly GameProcess _game;
    private readonly PlayerReader _reader;
    private readonly AimCheat _aim;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _running;

    /// <summary>조준선이 이 시간(ms) 이상 계속 적 위에 있어야 쏜다. 0 = 즉시.</summary>
    public volatile int ReactionDelayMs = 0;

    /// <summary>true 면 머리에 겹칠 때만 쏜다.</summary>
    public volatile bool HeadOnly = false;

    // 아래는 트리거봇 스레드에서만 접근 (Stop 은 Join 이후에 Release)
    private bool _pressed;
    private long _onSinceMs = -1;

    // 진단 (1초 집계)
    private long _nextReportMs = 1000;
    private int _loops, _onTarget, _presses, _notForeground;
    private string _lastHit = "";

    public TriggerBot(GameProcess game, PlayerReader reader, AimCheat aim, Action<string> log)
    {
        _game = game;
        _reader = reader;
        _aim = aim;
        _log = log;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TriggerBot",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        _log($"[트리거봇] 켜짐 — 지연 {ReactionDelayMs}ms · {(HeadOnly ? "머리만" : "머리+몸통")} · 게임이 맨 앞일 때만 동작");
    }

    public void Stop()
    {
        if (!_running && _thread == null) return;
        _running = false;
        _thread?.Join(500);
        _thread = null;
        Release();   // 스레드가 끝난 뒤라 경합 없음
        _log("[트리거봇] 꺼짐");
    }

    public void Dispose() => Stop();

    // -----------------------------------------------------------------
    //  루프
    // -----------------------------------------------------------------

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Release();
                _onSinceMs = -1;
                _log("[트리거봇 오류] " + ex.Message);
                Thread.Sleep(200);
            }
            Thread.Sleep(1);
        }
        Release();
    }

    private void Tick()
    {
        _loops++;
        long now = _clock.ElapsedMilliseconds;

        IntPtr gameWnd = _game.FindGameWindow();
        if (gameWnd == IntPtr.Zero || Win32.GetForegroundWindow() != gameWnd)
        {
            _notForeground++;
            _onSinceMs = -1;
            Release();
            Report(now);
            return;
        }

        if (IsCrosshairOnEnemy(out string hit))
        {
            _onTarget++;
            if (_onSinceMs < 0) _onSinceMs = now;

            if (!_pressed && now - _onSinceMs >= ReactionDelayMs)
            {
                Press();
                _presses++;
                _lastHit = hit;
            }
        }
        else
        {
            _onSinceMs = -1;
            Release();
        }

        Report(now);
    }

    private void Report(long now)
    {
        if (now < _nextReportMs) return;
        _nextReportMs = now + 1000;

        // 조준선이 적에 한 번이라도 걸린 초만 찍는다 (조용한 1초는 생략).
        if (_onTarget > 0 || _presses > 0)
        {
            _log($"[트리거봇] 판정 {_loops} · 조준선 위 {_onTarget} · 발사 시작 {_presses} · " +
                 $"게임 아님 {_notForeground}" + (_lastHit.Length > 0 ? $" · 마지막 {_lastHit}" : ""));
        }
        _loops = _onTarget = _presses = _notForeground = 0;
        _lastHit = "";
    }

    // -----------------------------------------------------------------
    //  판정
    // -----------------------------------------------------------------

    private bool IsCrosshairOnEnemy(out string hit)
    {
        hit = "";
        if (!_reader.TryReadLocal(out var me) || !_aim.Probe(out float yaw, out float pitch))
            return false;

        Vector3 eye = me.Position + new Vector3(0f, AimSolver.EyeOffsetY, 0f);
        Vector3 dir = Direction(yaw, pitch);

        foreach (var e in _reader.ReadLiveEnemies())
        {
            Vector3 root = e.State.Position;
            float dist = Vector3.Distance(eye, root);
            if (dist > MaxRangeM) continue;

            if (RayHitsSphere(eye, dir, root + new Vector3(0f, HeadOffsetY, 0f), HeadRadius))
            {
                hit = $"머리 {dist:F0}m";
                return true;
            }

            if (!HeadOnly &&
                RayHitsCapsule(eye, dir,
                               root + new Vector3(0f, BodyBottomY, 0f),
                               root + new Vector3(0f, BodyTopY, 0f),
                               BodyRadius))
            {
                hit = $"몸통 {dist:F0}m";
                return true;
            }
        }
        return false;
    }

    /// <summary>게임의 조준 방향 정의와 같다: Quaternion.Euler(pitch, yaw, 0) * forward.</summary>
    private static Vector3 Direction(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * (MathF.PI / 180f), p = pitchDeg * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(y) * MathF.Cos(p), -MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }

    /// <summary>광선(o, 단위벡터 d, t ≥ 0)이 구에 닿는가.</summary>
    private static bool RayHitsSphere(Vector3 o, Vector3 d, Vector3 center, float r)
    {
        Vector3 m = o - center;
        float b = Vector3.Dot(m, d);
        float c = Vector3.Dot(m, m) - r * r;
        if (c > 0f && b > 0f) return false;     // 밖에 있고 멀어지는 방향
        return b * b - c >= 0f;
    }

    /// <summary>
    /// 광선(o, 단위벡터 d, t ≥ 0)과 선분 a–b 사이 최단거리가 r 이하인가 (= 캡슐 관통).
    /// 무한 직선 해를 구한 뒤 두 번 번갈아 클램프한다. 축이 세로이고 광선이 대체로
    /// 수평이라 이 정도로 충분히 정확하다.
    /// </summary>
    private static bool RayHitsCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 bPt, float r)
    {
        Vector3 v = bPt - a;
        Vector3 w0 = o - a;
        float bb = Vector3.Dot(d, v);
        float cc = Vector3.Dot(v, v);
        float dd = Vector3.Dot(d, w0);
        float ee = Vector3.Dot(v, w0);
        float denom = cc - bb * bb;             // |d| = 1

        float s = denom > 1e-6f ? (ee - bb * dd) / denom : 0f;
        s = Math.Clamp(s, 0f, 1f);

        float t = MathF.Max(0f, Vector3.Dot(a + v * s - o, d));
        s = Math.Clamp(Vector3.Dot(o + d * t - a, v) / cc, 0f, 1f);
        t = MathF.Max(0f, Vector3.Dot(a + v * s - o, d));

        Vector3 diff = (o + d * t) - (a + v * s);
        return diff.LengthSquared() <= r * r;
    }

    // -----------------------------------------------------------------
    //  입력
    // -----------------------------------------------------------------

    private void Press()
    {
        if (_pressed) return;
        SendMouse(Win32.MOUSEEVENTF_LEFTDOWN);
        _pressed = true;
    }

    private void Release()
    {
        if (!_pressed) return;
        SendMouse(Win32.MOUSEEVENTF_LEFTUP);
        _pressed = false;
    }

    private static void SendMouse(uint flags)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            mi = new Win32.MOUSEINPUT { dwFlags = flags },
        };
        Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
    }
}
