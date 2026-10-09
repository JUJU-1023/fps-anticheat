using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CheatClient.Core;
using CheatClient.Overlay;

namespace CheatClient;

public partial class MainWindow : Window
{
    private readonly GameProcess _game = new();
    private AimCheat? _aim;
    private AimLock? _lock;
    private PlayerReader? _reader;
    private AutoAim? _auto;
    private EspOverlay? _esp;
    private TriggerBot? _trigger;

    // 조준각·위치 표시용 타이머 (UI 스레드)
    private readonly DispatcherTimer _display = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    public MainWindow()
    {
        InitializeComponent();
        _display.Tick += (_, _) => RefreshStatus();
        Closed += (_, _) =>
        {
            _display.Stop();
            _trigger?.Dispose();   // 누르고 있던 마우스 버튼부터 뗀다
            _trigger = null;
            _esp?.Close();         // 오버레이는 별도 최상위 창이라 직접 닫아야 프로세스가 끝난다
            _esp = null;
            _lock?.Stop();
            _reader?.Dispose();
            _game.Dispose();
        };
    }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        try
        {
            _game.Attach();

            _aim = new AimCheat(_game);
            _lock = new AimLock(_aim, LogFromAnyThread);
            _reader = new PlayerReader(_game, LogFromAnyThread);
            _auto = new AutoAim(_reader, _aim)
            {
                Log = LogFromAnyThread   // W11 Day 2: 1초 단위 자동 조준 진단
            };
            _trigger = new TriggerBot(_game, _reader, _aim, LogFromAnyThread);

            var monoBase = _game.GetModuleBase("mono-2.0-bdwgc.dll");

            StatusText.Text = "연결됨 · 해시 검증 통과";
            PidText.Text = $"PID: {_game.Pid}";
            MonoBaseText.Text = $"mono-2.0-bdwgc.dll: 0x{monoBase.ToInt64():X}";

            // 체인 검증: exe 의 ResolveChain 이 CE 와 같은 값을 읽는지.
            if (_aim.Probe(out float y, out float p))
                Log($"[검증] 조준 체인 OK — yaw={y:F1} pitch={p:F1} (CE 값과 같아야 함)");
            else
                Log("[검증] 조준 체인 해석 실패 — 매치 입장 전이면 정상 (입장하면 자동으로 읽힘)");

            // W11: 내 위치. 서 있으면 y ≈ 1.08 이어야 한다.
            if (_reader.TryReadLocal(out var me))
                Log($"[검증] 내 위치 OK — ({me.Position.X:F2}, {me.Position.Y:F2}, {me.Position.Z:F2}) tick {me.Tick}");
            else
                Log("[검증] 내 위치 읽기 실패 — 매치 입장 전이면 정상 (입장하면 자동으로 읽힘)");

            // W11 Day 4: ESP 가 겹칠 게임 창.
            if (_game.TryGetClientScreenRect(out int gx, out int gy, out int gw, out int gh))
                Log($"[검증] 게임 창 OK — {gw}×{gh} @({gx},{gy})");
            else
                Log("[검증] 게임 창을 찾지 못함 — 최소화돼 있지 않은지 확인");

            _reader.StartScanning();

            LockToggle.IsEnabled = true;
            AutoToggle.IsEnabled = true;
            EspToggle.IsEnabled = true;
            TriggerToggle.IsEnabled = true;
            _display.Start();

            Log("attach 성공. 적 보간기 스캔 시작 (5초 간격).");
            Log($"[자동조준 설정] 조준점 {(_auto.AimHead ? "머리" : "몸통")} · 반동보정 {(_auto.CompensateRecoil ? "ON" : "OFF")}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "실패";
            Log("[오류] " + ex.Message);
        }
    }

    private void RefreshStatus()
    {
        if (_aim == null) return;

        AimText.Text = _aim.Probe(out float y, out float p)
            ? $"조준: yaw {y:F1} / pitch {p:F1}"
            : "조준: (읽기 실패 — 게임 종료?)";

        if (_reader == null) return;

        PosText.Text = _reader.TryReadLocal(out var me)
            ? $"내 위치: ({me.Position.X:F2}, {me.Position.Y:F2}, {me.Position.Z:F2})"
            : "내 위치: (읽기 실패)";

        // ※ 이 호출도 PlayerReader 진단 카운터에 섞인다 (초당 5회, 자동 조준은 초당 약 65회).
        var enemies = _reader.ReadLiveEnemies();
        var lines = enemies.Select(e =>
            $"  ({e.State.Position.X:F2}, {e.State.Position.Y:F2}, {e.State.Position.Z:F2}) tick {e.State.Tick}");
        EnemyText.Text = $"적: 활성 {enemies.Count} / 추적 {_reader.TrackedCount}"
                       + (enemies.Count > 0 ? "\n" + string.Join("\n", lines) : "");
    }

    // --- 수동 조준 고정 (W10) ---

    private void OnLockChecked(object sender, RoutedEventArgs e)
    {
        if (_lock == null) return;
        if (AutoToggle.IsChecked == true) AutoToggle.IsChecked = false;   // 둘 중 하나만

        if (!TryReadAngles(out float yaw, out float pitch))
        {
            Log("[오류] yaw/pitch 입력값이 숫자가 아니다.");
            LockToggle.IsChecked = false;
            return;
        }
        _lock.SetProvider(null);
        _lock.SetTarget(yaw, pitch);
        _lock.Start();
    }

    private void OnLockUnchecked(object sender, RoutedEventArgs e)
        => _lock?.Stop();

    // --- 자동 조준 (W11) ---

    private void OnAutoChecked(object sender, RoutedEventArgs e)
    {
        if (_lock == null || _auto == null) return;
        if (LockToggle.IsChecked == true) LockToggle.IsChecked = false;   // 둘 중 하나만

        _lock.SetProvider(_auto.TryGetAim);
        _lock.Start();
        Log("[자동조준] 켜짐 — 우클릭을 누르고 있는 동안 1초마다 진단 출력");
    }

    private void OnAutoUnchecked(object sender, RoutedEventArgs e)
    {
        _lock?.Stop();
        _lock?.SetProvider(null);
    }

    // --- 월핵 ESP (W11 Day 4) ---

    private void OnEspChecked(object sender, RoutedEventArgs e)
    {
        if (_reader == null || _aim == null) return;

        _esp = new EspOverlay(_game, _reader, _aim, LogFromAnyThread);
        if (TryReadInt(EspLeadBox, out int lead)) _esp.LeadMs = lead;
        _esp.Show();
        Log("[ESP] 켜짐 — 게임 창을 클릭해 맨 앞으로 가져오면 표시된다");
    }

    private void OnEspUnchecked(object sender, RoutedEventArgs e)
    {
        _esp?.Close();
        _esp = null;
        Log("[ESP] 꺼짐");
    }

    /// <summary>예측(ms) 칸을 고치면 켜져 있는 ESP 에 바로 반영한다.</summary>
    private void OnEspLeadChanged(object sender, TextChangedEventArgs e)
    {
        if (_esp != null && TryReadInt(EspLeadBox, out int lead)) _esp.LeadMs = lead;
    }

    // --- 트리거봇 (W12) ---

    private void OnTriggerChecked(object sender, RoutedEventArgs e)
    {
        if (_trigger == null) return;
        ApplyTriggerSettings();
        _trigger.Start();
    }

    private void OnTriggerUnchecked(object sender, RoutedEventArgs e)
        => _trigger?.Stop();

    private void OnTriggerDelayChanged(object sender, TextChangedEventArgs e)
        => ApplyTriggerSettings();

    private void OnTriggerHeadOnlyChanged(object sender, RoutedEventArgs e)
        => ApplyTriggerSettings();

    private void ApplyTriggerSettings()
    {
        if (_trigger == null) return;
        if (TryReadInt(TriggerDelayBox, out int delay)) _trigger.ReactionDelayMs = Math.Clamp(delay, 0, 1000);
        _trigger.HeadOnly = TriggerHeadOnly?.IsChecked == true;
    }

    // --- 입력 칸 ---

    private static bool TryReadInt(TextBox? box, out int value)
    {
        value = 0;
        return box != null &&
               int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private bool TryReadAngles(out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        return float.TryParse(YawBox.Text,   NumberStyles.Float, CultureInfo.InvariantCulture, out yaw)
            && float.TryParse(PitchBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out pitch);
    }

    // --- 로그 ---

    private void Log(string msg)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
        LogBox.ScrollToEnd();
    }

    /// <summary>
    /// 백그라운드 스레드(AimLock 루프, 스캔 스레드, 트리거봇)에서 부른다.
    /// Invoke(동기)가 아니라 BeginInvoke(비동기)를 쓴다. 동기로 부르면
    ///   - 조준 루프가 매 로그마다 UI 스레드를 기다려 끊기고,
    ///   - 창을 닫을 때 UI 스레드가 _lock.Stop() 에서 루프 종료를 기다리는 동안
    ///     루프는 UI 스레드를 기다려 서로 멈출 수 있다(교착).
    /// </summary>
    private void LogFromAnyThread(string msg)
        => Dispatcher.BeginInvoke(() => Log(msg));
}
