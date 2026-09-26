using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntiCheatDashboard.Data;

namespace AntiCheatDashboard.Views;

/// <summary>
/// 플레이어 탭. 탭에 들어올 때와 새로고침 버튼으로만 다시 읽는다.
/// Day 4에 ActionPanel에 킥/밴 버튼이 붙는다.
/// </summary>
public partial class PlayersView : UserControl
{
    private bool _ready;
    private bool _suppress;
    private long? _selectedPlayerId;
    private int _detailRequest;

    public PlayersView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshAsync();
    }

    private async void Filter_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PlayerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var player = PlayerGrid.SelectedItem as PlayerRow;
        _selectedPlayerId = player?.PlayerId;
        await LoadDetailAsync(player);
    }

    private async Task RefreshAsync()
    {
        var exclude = ExcludeBox.IsChecked == true;
        var bots = BotsBox.IsChecked == true;
        var violatorsOnly = ViolatorsOnlyBox.IsChecked == true;

        StatusLine.Text = "불러오는 중...";
        try
        {
            var all = await StatsRepository.GetPlayersAsync(exclude, bots);
            var rows = violatorsOnly ? all.Where(r => r.HasViolations).ToList() : all;

            _suppress = true;
            PlayerRow? selected;
            try
            {
                PlayerGrid.ItemsSource = rows;
                selected = _selectedPlayerId is long id ? rows.FirstOrDefault(r => r.PlayerId == id) : null;
                PlayerGrid.SelectedItem = selected;
                if (selected != null) PlayerGrid.ScrollIntoView(selected);
            }
            finally
            {
                _suppress = false;
            }

            var violators = all.Count(r => r.HasViolations);
            var banned = all.Count(r => r.IsBanned);
            StatusLine.Text = $"갱신 {DateTime.Now:HH:mm:ss} · 플레이어 {rows.Count}명 표시 (전체 {all.Count}, 위반 {violators}, 밴 {banned})" +
                              (exclude ? " · 오염 데이터 제외 중" : "");

            await LoadDetailAsync(selected);
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"불러오기 실패: {ex.Message}";
        }
    }

    private async Task LoadDetailAsync(PlayerRow? player)
    {
        var request = ++_detailRequest;

        if (player is null)
        {
            DetailHeader.Text = "플레이어를 선택하면 매치별 이력이 표시됩니다";
            HistoryGrid.ItemsSource = null;
            return;
        }

        var header = $"{player.PlayerText}{(player.IsBot ? " [BOT]" : "")} · uid {player.Uid}" +
                     (player.IsBanned ? $" · {player.BanStatus}" : "");
        DetailHeader.Text = header + " — 불러오는 중...";
        try
        {
            var history = await StatsRepository.GetPlayerHistoryAsync(player.PlayerId, ExcludeBox.IsChecked == true);
            if (request != _detailRequest) return;

            HistoryGrid.ItemsSource = history;
            var violMatches = history.Count(h => h.HasViolations);
            DetailHeader.Text = $"{header} · 매치 {history.Count}개 중 위반 {violMatches}개";
        }
        catch (Exception ex)
        {
            if (request != _detailRequest) return;
            DetailHeader.Text = $"{header} — 불러오기 실패: {ex.Message}";
            HistoryGrid.ItemsSource = null;
        }
    }
}
