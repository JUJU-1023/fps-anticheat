#pragma warning disable CS0162 // UseGpuComposition=false 일 때 GPU 경로가 도달 불가 — 의도된 것 (W11 Day4e 참고)
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CheatClient.Core;
using CheatClient.Native;

namespace CheatClient.Overlay;

/// <summary>
/// 월핵 ESP 오버레이 (W11 Day 4).
///
/// 게임 화면 위에 투명하고 항상 위에 있고 클릭이 통과하는 창을 겹쳐,
/// 적 위치를 박스로 그린다. 게임 렌더링에 끼어들지 않는다(DirectX 후킹 없음).
/// 읽기만 한다 — 게임 메모리에 아무것도 쓰지 않는다.
///
/// ─ 동작 ─
///   매 렌더 프레임(CompositionTarget.Rendering, UI 스레드)마다
///     1. 게임 창이 맨 앞인지 확인. 아니면 오버레이를 1×1 로 접어 화면 밖에 둔다.
///     2. 게임 창 클라이언트 영역에 오버레이 위치·크기를 맞춘다(물리 픽셀 → DIP).
///     3. 내 위치·조준각·적 위치를 읽어 EspProjector 로 투영해 그린다.
///
/// ─ 렌더링 방식 (Day 4e) ─
///   UseGpuComposition = true
///     WPF 는 평소처럼 GPU 로 그리고, DWM 이 창을 투명 유리 영역으로 합성한다.
///     WS_EX_LAYERED + 알파 255 로 클릭 통과를 건다.
///     Day 4b 에서는 WPF 가 확장 스타일을 덮어써 클릭 통과가 풀렸다(화면이 막힘).
///     이제는 WM_STYLECHANGING 을 가로채 LAYERED 까지 지워지지 않게 고정한다.
///     → 실측(10/9): WM_STYLECHANGING 으로 가로채도 매초 다시 풀려 5회 만에 자동 비활성.
///       WPF GPU 창에는 LAYERED 가 유지되지 않는다. 클릭 통과와 양립 불가로 보류.
///       더 매끄럽게 하려면 Direct2D 오버레이(GameOverlay.Net 등)로 교체해야 한다.
///   UseGpuComposition = false   ← 현재 사용
///     WPF AllowsTransparency. 확실하지만 매 프레임 CPU 로 그려 1440p 에서 느리다.
///     회전 중 밀림은 LeadMs(예측)로 보정한다.
///
///   남는 지연(게임과 오버레이의 화면 출력 시점 차이)은 LeadMs 로 보정한다.
///
/// ─ 클릭 통과 보장 ─
///   ① WM_STYLECHANGING 을 가로채 확장 스타일 변경 시 항상 필요한 비트를 붙인다.
///   ② WM_MOUSEACTIVATE 에 MA_NOACTIVATE 로 답해 클릭 활성화를 거부한다.
///   ③ 그릴 게 없으면(게임 종료·최소화·다른 창이 맨 앞) 1×1 로 접어 화면 밖에 둔다.
///      창을 숨기지는 않는다 — 다시 보이게 할 때 Windows 가 활성화해 포커스를 빼앗는다.
///   ④ 그래도 스타일이 반복해서 풀리면 스스로 비활성화한다(접힌 채로 둠).
///
/// ─ 전제 ─
///   게임이 Fullscreen Window(테두리 없는 창) 모드여야 한다. 프리즈 빌드는 fullscreenMode: 1.
///
/// ─ 탐지 ─
///   서버는 이걸 볼 수 없다. 서버 권위 모델에서 ESP 는 순수 정보 노출이고,
///   행동(벽 너머 추적·즉시 반응)으로 이어질 때만 L2(V-LOS/V-TIME)·L3 에 흔적이 남는다.
/// </summary>
public sealed class EspOverlay : Window
{
    /// <summary>true = GPU(DWM 합성), false = CPU(AllowsTransparency). 화면이 검게 덮이면 false.</summary>
    private const bool UseGpuComposition = false;   // Day 4e: WPF GPU 창에는 LAYERED 가 붙지 않아 클릭 통과 불가 → CPU 고정

    private const double MaxLeadMs = 150;
    private const double StyleCheckIntervalMs = 1000;
    private const int    MaxStyleRepairs = 5;

    // 클릭 통과·Alt+Tab 제외·포커스 안 뺏기. GPU 방식이면 LAYERED 도 우리가 고정한다.
    // (CPU 방식에서는 AllowsTransparency 가 WPF 쪽에서 LAYERED 를 관리하므로 건드리지 않는다)
    private const uint ForcedExStyle =
        (uint)(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE |
               (UseGpuComposition ? Win32.WS_EX_LAYERED : 0));

    private const int WM_STYLECHANGING = 0x007C;

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public uint styleOld;
        public uint styleNew;
    }

    private readonly GameProcess _game;
    private readonly PlayerReader _reader;
    private readonly AimCheat _aim;
    private readonly Action<string> _log;
    private readonly EspCanvas _canvas = new();
    private readonly AngleHistory _angles = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IntPtr _hwnd = IntPtr.Zero;
    private TimeSpan _lastRender = TimeSpan.MinValue;
    private string _lastGeometry = "";
    private string _lastError = "";
    private double _nextStyleCheck;
    private int _styleRepairs;
    private int _focusSteals;
    private bool _disabled;

    private int _frames;
    private double _fpsWindowStart;
    private int _fps;

    private double _leadMs;

    /// <summary>
    /// 조준각 예측(ms). + 면 회전 방향으로 앞당겨 그리고, − 면 과거 각도로 그린다.
    /// 돌릴 때 박스가 적보다 늦게 따라오면 키우고, 먼저 가면 줄인다.
    /// </summary>
    public double LeadMs
    {
        get => _leadMs;
        set => _leadMs = Math.Clamp(value, -MaxLeadMs, MaxLeadMs);
    }

    /// <summary>화면 아래 가운데에서 적 발밑까지 선을 긋는다. 화면 밖·멀리 있는 적을 찾기 쉽다.</summary>
    public bool ShowSnaplines
    {
        get => _canvas.ShowSnaplines;
        set => _canvas.ShowSnaplines = value;
    }

    public EspOverlay(GameProcess game, PlayerReader reader, AimCheat aim, Action<string> log)
    {
        _game = game;
        _reader = reader;
        _aim = aim;
        _log = log;

        Title = "ESP";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = !UseGpuComposition;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Collapse();                 // 처음엔 접힌 상태로 띄우고, 게임이 맨 앞이면 펼친다
        Content = _canvas;

        // 표시되기 전에 스타일을 건다. 그래야 첫 Show() 에서도 클릭 통과·비활성이다.
        SourceInitialized += (_, _) => ConfigureWindow();
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private void ConfigureWindow()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);

        if (UseGpuComposition)
        {
            // WPF 배경을 투명으로 두고, DWM 이 창 전체를 유리 영역으로 합성하게 한다.
            if (src?.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = Colors.Transparent;

            var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }

        EnsureExStyle();
        if (UseGpuComposition) ApplyLayeredAlpha();

        _log($"[ESP] 오버레이 준비 — {(UseGpuComposition ? "GPU(DWM 합성)" : "CPU(AllowsTransparency)")} · 클릭 통과 고정");
    }

    /// <summary>
    /// 레이어드 창은 속성을 한 번 지정해야 보인다. 알파 255 = 불투명도 그대로,
    /// 픽셀 투명도는 DWM 합성이 처리한다. LAYERED 가 다시 붙은 뒤에도 불러야 한다.
    /// </summary>
    private void ApplyLayeredAlpha()
        => Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);

    /// <summary>필요한 확장 스타일 비트가 다 있는지 확인하고, 빠져 있으면 붙인다. 붙였으면 true.</summary>
    private bool EnsureExStyle()
    {
        if (_hwnd == IntPtr.Zero) return false;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & ForcedExStyle) == ForcedExStyle) return false;

        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | ForcedExStyle));
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // 누가(WPF 포함) 확장 스타일을 바꾸든 필요한 비트는 항상 남긴다.
            case WM_STYLECHANGING when wParam.ToInt32() == Win32.GWL_EXSTYLE:
                var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
                if ((ss.styleNew & ForcedExStyle) != ForcedExStyle)
                {
                    ss.styleNew |= ForcedExStyle;
                    Marshal.StructureToPtr(ss, lParam, false);
                }
                break;   // 기본 처리는 그대로 진행

            // 마우스로 이 창을 활성화하려는 시도를 거부한다.
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>1×1 로 접어 화면 밖에 둔다. 무엇도 덮지 않는다.</summary>
    private void Collapse()
    {
        _canvas.Clear();
        if (Width > 1 || Height > 1 || Left > -10 || Top > -10)
        {
            Left = -10; Top = -10; Width = 1; Height = 1;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering 이벤트는 한 프레임에 여러 번 올 수 있다. 같은 프레임이면 건너뛴다.
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRender) return;
            _lastRender = re.RenderingTime;
        }

        try
        {
            UpdateFrame();
        }
        catch (Exception ex)
        {
            Collapse();
            string msg = "[ESP 오류] " + ex.Message;
            if (msg != _lastError) { _log(msg); _lastError = msg; }
        }
    }

    private void UpdateFrame()
    {
        if (_disabled) { Collapse(); return; }

        double now = _clock.Elapsed.TotalMilliseconds;
        CountFrame(now);

        IntPtr gameWnd = _game.FindGameWindow();
        if (!Guard(gameWnd, now)) return;

        // 1. 게임이 맨 앞일 때만 펼친다.
        if (gameWnd == IntPtr.Zero ||
            Win32.GetForegroundWindow() != gameWnd ||
            !_game.TryGetClientScreenRect(out int px, out int py, out int pw, out int ph))
        {
            Collapse();
            return;
        }

        // 2. 게임 클라이언트 영역에 맞춘다. Win32 는 물리 픽셀, WPF 는 DIP.
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = px / dpi.DpiScaleX, top = py / dpi.DpiScaleY;
        double width = pw / dpi.DpiScaleX, height = ph / dpi.DpiScaleY;

        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5 ||
            Math.Abs(Width - width) > 0.5 || Math.Abs(Height - height) > 0.5)
        {
            Left = left; Top = top; Width = width; Height = height;
        }

        string geo = $"[ESP] 게임 화면 {pw}×{ph} @({px},{py}) · 배율 {dpi.DpiScaleX:P0}";
        if (geo != _lastGeometry) { _log(geo); _lastGeometry = geo; }

        // 3. 읽고 투영해서 그린다.
        double vw = _canvas.ActualWidth, vh = _canvas.ActualHeight;
        if (vw < 2 || vh < 2) return;   // 크기 반영 전 첫 프레임

        if (!_reader.TryReadLocal(out var me) || !_aim.Probe(out float rawYaw, out float rawPitch))
        {
            _canvas.Update(Array.Empty<EspCanvas.Item>(), "ESP · 내 위치/조준각 읽기 실패");
            return;
        }

        _angles.Push(now, rawYaw, rawPitch);
        _angles.Sample(now + _leadMs, out float yaw, out float pitch);

        Vector3 cam = EspProjector.CameraPosition(me);
        var enemies = _reader.ReadLiveEnemies();
        var items = new List<EspCanvas.Item>(enemies.Count);

        foreach (var e in enemies)
        {
            Vector3 root = e.State.Position;

            if (!EspProjector.TryProject(cam, yaw, pitch, root + new Vector3(0f, EspProjector.FeetOffsetY, 0f),
                                         vw, vh, out double fx, out double fy, out _))
                continue;   // 내 뒤
            if (!EspProjector.TryProject(cam, yaw, pitch, root + new Vector3(0f, EspProjector.TopOffsetY, 0f),
                                         vw, vh, out double tx, out double ty, out _))
                continue;
            EspProjector.TryProject(cam, yaw, pitch, root + new Vector3(0f, AimSolver.HeadOffsetY, 0f),
                                    vw, vh, out double hx, out double hy, out _);

            double boxH = Math.Max(4.0, fy - ty);
            double boxW = boxH * EspProjector.BoxAspect;
            double cx = (fx + tx) * 0.5;
            var box = new Rect(cx - boxW * 0.5, ty, boxW, boxH);

            // 화면에서 한참 벗어난 건 그리지 않는다 (스냅라인만 의미 없이 길어진다).
            if (box.Right < -100 || box.Left > vw + 100 || box.Bottom < -100 || box.Top > vh + 100)
                continue;

            float dist = Vector3.Distance(cam, root);
            items.Add(new EspCanvas.Item(box, new Point(hx, hy), new Point(fx, fy), $"{dist:F0}m"));
        }

        string lead = _leadMs == 0 ? "0" : _leadMs.ToString("+0;-0", CultureInfo.InvariantCulture);
        string mode = UseGpuComposition ? "GPU" : "CPU";
        _canvas.Update(items, $"ESP · 화면 {items.Count} / 활성 {enemies.Count} · {mode} {_fps}fps · 예측 {lead}ms");
    }

    /// <summary>
    /// 안전장치. 포커스를 가져갔으면 게임으로 되돌리고, 클릭 통과가 풀렸으면 다시 건다.
    /// 반복해서 풀리면 스스로 비활성화한다. 계속 진행해도 되면 true.
    /// </summary>
    private bool Guard(IntPtr gameWnd, double now)
    {
        if (_hwnd == IntPtr.Zero) return true;

        if (gameWnd != IntPtr.Zero && Win32.GetForegroundWindow() == _hwnd)
        {
            Win32.SetForegroundWindow(gameWnd);
            _focusSteals++;
            if (_focusSteals == 1 || _focusSteals % 50 == 0)
                _log($"[ESP] 오버레이가 포커스를 가져가 게임으로 되돌림 (누적 {_focusSteals}회)");
        }

        if (now >= _nextStyleCheck)
        {
            _nextStyleCheck = now + StyleCheckIntervalMs;
            if (EnsureExStyle())
            {
                if (UseGpuComposition) ApplyLayeredAlpha();
                _styleRepairs++;
                _log($"[ESP] 클릭 통과가 풀려 있어 다시 걸었음 ({_styleRepairs}/{MaxStyleRepairs})");

                if (_styleRepairs >= MaxStyleRepairs)
                {
                    _disabled = true;
                    Collapse();
                    _log("[ESP] 클릭 통과를 유지할 수 없어 비활성화함. ESP 토글을 끄고 로그를 확인할 것.");
                    return false;
                }
            }
        }
        return true;
    }

    private void CountFrame(double now)
    {
        _frames++;
        if (now - _fpsWindowStart >= 1000)
        {
            _fps = (int)Math.Round(_frames * 1000.0 / (now - _fpsWindowStart));
            _frames = 0;
            _fpsWindowStart = now;
        }
    }

    /// <summary>
    /// 최근 조준각 기록. 임의 시점의 각도를 보간(과거) 또는 외삽(미래)으로 돌려준다.
    ///
    /// 외삽 속도는 직전 샘플 하나가 아니라 VelocityWindowMs 구간으로 구한다.
    /// 게임이 오버레이보다 느리게 갱신되면 같은 값이 연달아 읽혀서,
    /// 바로 앞 샘플과만 비교하면 속도가 0 과 큰 값 사이를 오간다.
    /// </summary>
    private sealed class AngleHistory
    {
        private const int Capacity = 64;
        private const double VelocityWindowMs = 50;
        private const double MaxExtrapolateMs = 100;

        private readonly double[] _t = new double[Capacity];
        private readonly float[] _yaw = new float[Capacity];
        private readonly float[] _pitch = new float[Capacity];
        private int _head = -1;    // 가장 최근 샘플 위치
        private int _count;

        public void Push(double t, float yaw, float pitch)
        {
            _head = (_head + 1) % Capacity;
            _t[_head] = t;
            _yaw[_head] = yaw;
            _pitch[_head] = pitch;
            if (_count < Capacity) _count++;
        }

        /// <summary>back = 0 이 최신, 1 이 그 직전 …</summary>
        private int Idx(int back) => ((_head - back) % Capacity + Capacity) % Capacity;

        public void Sample(double t, out float yaw, out float pitch)
        {
            int n = Idx(0);
            yaw = _yaw[n];
            pitch = _pitch[n];
            if (_count < 2) return;

            // 미래 → 최근 구간의 회전 속도로 외삽
            if (t >= _t[n])
            {
                int k = 1;
                while (k < _count - 1 && _t[n] - _t[Idx(k)] < VelocityWindowMs) k++;
                int o = Idx(k);
                double dt = _t[n] - _t[o];
                if (dt < 1) return;

                double ahead = Math.Min(t - _t[n], MaxExtrapolateMs);
                yaw = WrapYaw(_yaw[n] + (float)(AimSolver.DeltaAngle(_yaw[n], _yaw[o]) / dt * ahead));
                pitch = Math.Clamp(_pitch[n] + (float)((_pitch[n] - _pitch[o]) / dt * ahead), -89f, 89f);
                return;
            }

            // 과거 → 두 샘플 사이 보간
            for (int k = 0; k < _count - 1; k++)
            {
                int newer = Idx(k), older = Idx(k + 1);
                if (_t[older] <= t)
                {
                    double span = _t[newer] - _t[older];
                    float f = span <= 0 ? 1f : (float)((t - _t[older]) / span);
                    yaw = WrapYaw(_yaw[older] + AimSolver.DeltaAngle(_yaw[newer], _yaw[older]) * f);
                    pitch = _pitch[older] + (_pitch[newer] - _pitch[older]) * f;
                    return;
                }
            }

            int oldest = Idx(_count - 1);
            yaw = _yaw[oldest];
            pitch = _pitch[oldest];
        }

        private static float WrapYaw(float y)
        {
            y %= 360f;
            return y < 0f ? y + 360f : y;
        }
    }
}

/// <summary>ESP 그리기. 매 프레임 Update → InvalidateVisual → OnRender.</summary>
internal sealed class EspCanvas : FrameworkElement
{
    internal readonly record struct Item(Rect Box, Point Head, Point Feet, string Label);

    private static readonly Pen BoxPen     = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 60, 200)), 1.6));
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3.6));
    private static readonly Pen SnapPen    = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 60, 200)), 1.0));
    private static readonly Brush HeadBrush = Frozen(new SolidColorBrush(Color.FromRgb(255, 230, 0)));
    private static readonly Brush LabelBg   = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Typeface Face   = new("Consolas");

    public bool ShowSnaplines = true;

    private IReadOnlyList<Item> _items = Array.Empty<Item>();
    private string _status = "";

    public EspCanvas()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    public void Update(IReadOnlyList<Item> items, string status)
    {
        _items = items;
        _status = status;
        InvalidateVisual();
    }

    /// <summary>아무것도 그리지 않는다. 이미 비어 있으면 다시 그리지 않는다.</summary>
    public void Clear()
    {
        if (_items.Count == 0 && _status.Length == 0) return;
        Update(Array.Empty<Item>(), "");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bottomCenter = new Point(ActualWidth * 0.5, ActualHeight);

        foreach (var it in _items)
        {
            if (ShowSnaplines) dc.DrawLine(SnapPen, bottomCenter, it.Feet);

            dc.DrawRectangle(null, OutlinePen, it.Box);
            dc.DrawRectangle(null, BoxPen, it.Box);

            double r = Math.Clamp(it.Box.Width * 0.08, 2.0, 5.0);
            dc.DrawEllipse(HeadBrush, null, it.Head, r, r);

            var ft = Text(it.Label, 12, ppd);
            var pos = new Point(it.Box.Left + it.Box.Width * 0.5 - ft.Width * 0.5, it.Box.Bottom + 2);
            dc.DrawRectangle(LabelBg, null, new Rect(pos.X - 3, pos.Y - 1, ft.Width + 6, ft.Height + 2));
            dc.DrawText(ft, pos);
        }

        if (_status.Length > 0)
        {
            var st = Text(_status, 13, ppd);
            dc.DrawRectangle(LabelBg, null, new Rect(8, 8, st.Width + 12, st.Height + 6));
            dc.DrawText(st, new Point(14, 11));
        }
    }

    private static FormattedText Text(string s, double size, double ppd)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Brushes.White, ppd);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
