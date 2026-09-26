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

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunTestAsync();

    private async void TestButton_Click(object sender, RoutedEventArgs e) => await RunTestAsync();

    private async Task RunTestAsync()
    {
        TestButton.IsEnabled = false;
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = "연결 테스트 중...";
        DiagTab.IsSelected = true;
        DiagOutput.Text = "";

        try
        {
            var (ok, lines) = await ConnectionTest.RunAsync();
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
    }
}
