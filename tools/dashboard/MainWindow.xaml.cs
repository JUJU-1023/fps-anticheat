using System.Windows;
using System.Windows.Media;
using AntiCheatDashboard.Data;
using MySqlConnector;

namespace AntiCheatDashboard;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ServerText.Text = Db.Config.Display;
    }

    // 시작할 때: 진단은 조용히 돌리고, 통과하면 실시간 탭에 머무름. 실패하면 진단 탭으로.
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunTestAsync(showDiag: false);

    // 버튼으로 돌릴 때: 항상 진단 탭을 보여줌
    private async void TestButton_Click(object sender, RoutedEventArgs e) => await RunTestAsync(showDiag: true);

    private async Task RunTestAsync(bool showDiag)
    {
        TestButton.IsEnabled = false;
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = "연결 테스트 중...";
        if (showDiag) DiagTab.IsSelected = true;
        DiagOutput.Text = "";

        var ok = false;
        try
        {
            var (passed, lines) = await ConnectionTest.RunAsync();
            ok = passed;
            DiagOutput.Text = string.Join(Environment.NewLine, lines);
            StatusDot.Fill = ok ? Brushes.LimeGreen : Brushes.Orange;
            StatusText.Text = ok ? "연결됨 · 권한 정상" : "연결됨 · 권한 확인 필요";
        }
        catch (MySqlException ex)
        {
            DiagOutput.Text = $"[FAIL] 접속 실패 ({ex.Number}){Environment.NewLine}{ex.Message}";
            StatusDot.Fill = Brushes.Red;
            StatusText.Text = "연결 실패";
        }
        catch (Exception ex)
        {
            DiagOutput.Text = $"[FAIL] {ex.GetType().Name}{Environment.NewLine}{ex.Message}";
            StatusDot.Fill = Brushes.Red;
            StatusText.Text = "오류";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }

        if (!ok) DiagTab.IsSelected = true;
    }
}
