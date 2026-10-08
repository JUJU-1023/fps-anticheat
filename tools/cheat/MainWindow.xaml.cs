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

    // 조준각 표시용 타이머 (UI 스레드)
    private readonly DispatcherTimer _display = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    public MainWindow()
    {
        InitializeComponent();
        _display.Tick += (_, _) => RefreshAim();
        Closed += (_, _) =>
        {
            _lock?.Stop();
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

            var monoBase = _game.GetModuleBase("mono-2.0-bdwgc.dll");

            StatusText.Text = "연결됨 · 해시 검증 통과";
            PidText.Text = $"PID: {_game.Pid}";
            MonoBaseText.Text = $"mono-2.0-bdwgc.dll: 0x{monoBase.ToInt64():X}";

            // 체인 검증: exe 의 ResolveChain 이 CE 와 같은 값을 읽는지.
            if (_aim.Probe(out float y, out float p))
                Log($"[검증] 체인 OK — yaw={y:F1} pitch={p:F1} (CE 값과 같아야 함)");
            else
                Log("[검증] 체인 해석 실패 — 오프셋/역참조 확인 필요");

            LockToggle.IsEnabled = true;
            _display.Start();

            Log("attach 성공. 조준 주소 확보.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "실패";
            Log("[오류] " + ex.Message);
        }
    }

    private void RefreshAim()
    {
        if (_aim == null) return;
        if (_aim.Probe(out float y, out float p))
            AimText.Text = $"조준: yaw {y:F1} / pitch {p:F1}";
        else
            AimText.Text = "조준: (읽기 실패 — 게임 종료?)";
    }

    // --- 조준 고정 토글 ---

    private void OnLockChecked(object sender, RoutedEventArgs e)
    {
        if (_lock == null) return;
        if (!TryReadAngles(out float yaw, out float pitch))
        {
            Log("[오류] yaw/pitch 입력값이 숫자가 아니다.");
            LockToggle.IsChecked = false;
            return;
        }
        _lock.SetTarget(yaw, pitch);
        _lock.Start();
    }

    private void OnLockUnchecked(object sender, RoutedEventArgs e)
        => _lock?.Stop();

    private bool TryReadAngles(out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        return float.TryParse(YawBox.Text,   NumberStyles.Float, CultureInfo.InvariantCulture, out yaw)
            && float.TryParse(PitchBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out pitch);
    }

    // --- 로그 ---

    private void Log(string msg)
        => LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");

    private void LogFromAnyThread(string msg)
        => Dispatcher.Invoke(() => Log(msg));
}
