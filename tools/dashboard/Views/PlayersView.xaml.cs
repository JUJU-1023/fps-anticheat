using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntiCheatDashboard.Data;

namespace AntiCheatDashboard.Views;

/// <summary>
/// 플레이어 탭. 탭에 들어올 때와 새로고침 버튼으로만 다시 읽는다.
/// 아래쪽에서 매치별 이력과 제재 이력을 보고, 킥/밴 발행과 해제를 한다.
/// </summary>
public partial class PlayersView : UserControl
{
    private bool _ready;
    private bool _suppress;
    private long? _selectedPlayerId;
    private int _detailRequest;
    private string? _flash;          // 발행/해제 직후 상태줄에 잠깐 붙일 문구

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

    private void BanGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RevokeButton.IsEnabled = BanGrid.SelectedItem is BanRow { IsRevocable: true };
        RevokeButton.Content = BanGrid.SelectedItem is BanRow { IsPending: true } ? "선택한 킥 취소" : "선택한 제재 해제";
    }

    // ───────── 제재 ─────────

    private async void Sanction_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerGrid.SelectedItem is not PlayerRow player || player.IsBot) return;

        var dialog = new BanDialog(player.PlayerId, $"{player.PlayerText} · uid {player.Uid}")
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        _flash = dialog.ResultSummary;
        BansTab.IsSelected = true;
        await RefreshAsync();
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (BanGrid.SelectedItem is not BanRow { IsRevocable: true } ban) return;
        if (PlayerGrid.SelectedItem is not PlayerRow player) return;

        var what = ban.IsBan ? $"밴 #{ban.Id} ({ban.PeriodText})" : $"킥 #{ban.Id}";
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"{player.PlayerText}\n\n{what}을(를) 해제합니다.\n사유: {ban.Reason}",
            "제재 해제 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var ok = await BanRepository.RevokeAsync(ban.Id);
            _flash = ok ? $"{what} 해제됨" : $"{what}은(는) 이미 해제된 상태";
        }
        catch (Exception ex)
        {
            _flash = $"해제 실패: {ex.Message}";
        }
        await RefreshAsync();
    }

    // ───────── 갱신 ─────────

    private async Task RefreshAsync()
    {
        var exclude = ExcludeBox.IsChecked == true;
        var bots = BotsBox.IsChecked == true;
        var violatorsOnly = ViolatorsOnlyBox.IsChecked == true;

        StatusLine.Text = "불러오는 중...";
        try
        {
            var all = await StatsRepository.GetPlayersAsync(exclude, bots);
            var rows = violatorsOnly ? all.Where(r => r.HasViolations || r.IsBanned).ToList() : all;

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
            StatusLine.Text = (_flash is null ? "" : $"{_flash} · ") +
                              $"갱신 {DateTime.Now:HH:mm:ss} · 플레이어 {rows.Count}명 표시 (전체 {all.Count}, 위반 {violators}, 밴 {banned})" +
                              (exclude ? " · 오염 데이터 제외 중" : "");
            _flash = null;

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

        SanctionButton.IsEnabled = player is { IsBot: false };
        RevokeButton.IsEnabled = false;

        if (player is null)
        {
            DetailHeader.Text = "플레이어를 선택하면 매치별 이력과 제재 이력이 표시됩니다";
            HistoryGrid.ItemsSource = null;
            BanGrid.ItemsSource = null;
            BansTab.Header = "제재 이력";
            return;
        }

        var header = $"{player.PlayerText}{(player.IsBot ? " [BOT]" : "")} · uid {player.Uid}" +
                     (player.IsBanned ? $" · {player.BanStatus}" : "");
        DetailHeader.Text = header + " — 불러오는 중...";
        try
        {
            var historyTask = StatsRepository.GetPlayerHistoryAsync(player.PlayerId, ExcludeBox.IsChecked == true);
            var bansTask = BanRepository.GetForPlayerAsync(player.PlayerId);
            await Task.WhenAll(historyTask, bansTask);
            if (request != _detailRequest) return;   // 그 사이 다른 플레이어를 선택함

            var history = historyTask.Result;
            var bans = bansTask.Result;

            HistoryGrid.ItemsSource = history;
            BanGrid.ItemsSource = bans;
            BansTab.Header = bans.Count == 0 ? "제재 이력" : $"제재 이력 ({bans.Count})";

            var violMatches = history.Count(h => h.HasViolations);
            DetailHeader.Text = $"{header} · 매치 {history.Count}개 중 위반 {violMatches}개";
        }
        catch (Exception ex)
        {
            if (request != _detailRequest) return;
            DetailHeader.Text = $"{header} — 불러오기 실패: {ex.Message}";
            HistoryGrid.ItemsSource = null;
            BanGrid.ItemsSource = null;
        }
    }
}
