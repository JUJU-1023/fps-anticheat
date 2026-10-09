using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CheatClient.Core;

namespace CheatClient;

public partial class MainWindow : Window
{
    private readonly GameProcess _game = new();
    private AimCheat? _aim;
    private AimLock? _lock;
    private PlayerReader? _reader;
    private AutoAim? _auto;

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
                Log = LogFromAnyThread,   // W11 Day 2: 1초 단위 자동 조준 진단
                //AimHead = true            // W11 D3 S3: 머리 조준. S3 끝나면 지우거나 false 로
            };

            var monoBase = _game.GetModuleBase("mono-2.0-bdwgc.dll");

            StatusText.Text = "연결됨 · 해시 검증 통과";
            PidText.Text = $"PID: {_game.Pid}";
            MonoBaseText.Text = $"mono-2.0-bdwgc.dll: 0x{monoBase.ToInt64():X}";

            // 체인 검증: exe 의 ResolveChain 이 CE 와 같은 값을 읽는지.
            if (_aim.Probe(out float y, out float p))
                Log($"[검증] 조준 체인 OK — yaw={y:F1} pitch={p:F1} (CE 값과 같아야 함)");
            else
                Log("[검증] 조준 체인 해석 실패 — 오프셋/역참조 확인 필요");

            // W11: 내 위치. 서 있으면 y ≈ 1.08 이어야 한다.
            if (_reader.TryReadLocal(out var me))
                Log($"[검증] 내 위치 OK — ({me.Position.X:F2}, {me.Position.Y:F2}, {me.Position.Z:F2}) tick {me.Tick}");
            else
                Log("[검증] 내 위치 읽기 실패 — stateBuffer 오프셋 확인 필요");

            _reader.StartScanning();

            LockToggle.IsEnabled = true;
            AutoToggle.IsEnabled = true;
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
    /// 백그라운드 스레드(AimLock 루프, 스캔 스레드)에서 부른다.
    /// Invoke(동기)가 아니라 BeginInvoke(비동기)를 쓴다. 동기로 부르면
    ///   - 조준 루프가 매 로그마다 UI 스레드를 기다려 끊기고,
    ///   - 창을 닫을 때 UI 스레드가 _lock.Stop() 에서 루프 종료를 기다리는 동안
    ///     루프는 UI 스레드를 기다려 서로 멈출 수 있다(교착).
    /// </summary>
    private void LogFromAnyThread(string msg)
        => Dispatcher.BeginInvoke(() => Log(msg));
}
