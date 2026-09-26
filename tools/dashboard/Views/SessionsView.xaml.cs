using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntiCheatDashboard.Data;

namespace AntiCheatDashboard.Views;

/// <summary>
/// 세션 탭. 폴링하지 않고 탭에 들어올 때와 새로고침 버튼으로만 다시 읽는다
/// (combat_events 전체 집계라 실시간 탭보다 무겁다).
/// </summary>
public partial class SessionsView : UserControl
{
    private const string AllText = "전체";

    private bool _ready;
    private bool _suppress;
    private long? _selectedMatchId;
    private int _detailRequest;   // 빠르게 선택을 바꿀 때 늦게 도착한 옛 결과를 버리기 위한 번호

    public SessionsView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshAsync();
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) await RefreshAsync();
    }

    private async void Filter_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void MatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var match = MatchGrid.SelectedItem as MatchRow;
        _selectedMatchId = match?.Id;
        await LoadDetailAsync(match);
    }

    private async Task RefreshAsync()
    {
        var label = (LabelBox.SelectedItem as ComboBoxItem)?.Content as string;
        var exclude = ExcludeBox.IsChecked == true;

        StatusLine.Text = "불러오는 중...";
        try
        {
            var rows = await StatsRepository.GetMatchesAsync(label is null or AllText ? null : label, exclude);

            _suppress = true;
            MatchRow? selected;
            try
            {
                MatchGrid.ItemsSource = rows;
                selected = _selectedMatchId is long id ? rows.FirstOrDefault(r => r.Id == id) : null;
                MatchGrid.SelectedItem = selected;
                if (selected != null) MatchGrid.ScrollIntoView(selected);
            }
            finally
            {
                _suppress = false;
            }

            var cheat = rows.Count(r => r.Label == "cheat");
            var withViol = rows.Count(r => r.ViolationTotal > 0);
            StatusLine.Text = $"갱신 {DateTime.Now:HH:mm:ss} · 매치 {rows.Count}개 (cheat {cheat}, 위반 발생 {withViol})" +
                              (exclude ? " · 오염 데이터 제외 중" : "");

            await LoadDetailAsync(selected);
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"불러오기 실패: {ex.Message}";
        }
    }

    private async Task LoadDetailAsync(MatchRow? match)
    {
        var request = ++_detailRequest;

        if (match is null)
        {
            DetailHeader.Text = "매치를 선택하면 참가자별 전적이 표시됩니다";
            PlayerGrid.ItemsSource = null;
            return;
        }

        DetailHeader.Text = $"매치 #{match.Id} ({match.Label}) · {match.StartText} · {match.DurationText} — 불러오는 중...";
        try
        {
            var players = await StatsRepository.GetMatchPlayersAsync(match.Id, ExcludeBox.IsChecked == true);
            if (request != _detailRequest) return; // 그 사이 다른 매치를 선택함

            PlayerGrid.ItemsSource = players;
            var humans = players.Count(p => !p.IsBot);
            var bots = players.Count - humans;
            DetailHeader.Text = $"매치 #{match.Id} ({match.Label}) · {match.StartText} · {match.DurationText}" +
                                $" · 참가 {humans}명" + (bots > 0 ? $" + 봇 {bots}" : "") +
                                (string.IsNullOrWhiteSpace(match.Notes) ? "" : $" · {match.Notes}");
        }
        catch (Exception ex)
        {
            if (request != _detailRequest) return;
            DetailHeader.Text = $"매치 #{match.Id} 불러오기 실패: {ex.Message}";
            PlayerGrid.ItemsSource = null;
        }
    }
}
