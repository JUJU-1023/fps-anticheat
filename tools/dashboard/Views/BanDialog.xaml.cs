using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AntiCheatDashboard.Data;

namespace AntiCheatDashboard.Views;

/// <summary>
/// 킥/밴 발행 창. 플레이어 탭과 실시간 위반 탭 양쪽에서 연다.
/// 이미 유효한 밴이 있으면 새 밴은 막고(먼저 해제), 킥은 언제든 가능하다.
/// </summary>
public partial class BanDialog : Window
{
    private readonly long _playerId;
    private readonly long? _preselectViolationId;

    private bool _ready;
    private bool _reasonAuto = true;   // 사용자가 사유를 직접 고치기 전까지 근거 위반에 맞춰 자동으로 채움
    private bool _settingReason;

    /// <summary>발행 성공 시 새 bans.id</summary>
    public long? IssuedBanId { get; private set; }

    /// <summary>발행 성공 시 한 줄 요약 (호출한 탭의 상태줄 표시용)</summary>
    public string ResultSummary { get; private set; } = "";

    public BanDialog(long playerId, string playerText, long? preselectViolationId = null)
    {
        InitializeComponent();
        _playerId = playerId;
        _preselectViolationId = preselectViolationId;
        PlayerTitle.Text = playerText;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CurrentStatus.Text = "현재 상태 확인 중...";
        ExecuteButton.IsEnabled = false;
        try
        {
            var bansTask = BanRepository.GetForPlayerAsync(_playerId);
            var violTask = ViolationRepository.GetRecentAsync(new ViolationFilter
            {
                PlayerId = _playerId,
                ApplyExclusions = false,
                Limit = 30,
            });
            await Task.WhenAll(bansTask, violTask);

            var active = bansTask.Result.FirstOrDefault(b => b.IsBan && b.IsActive);
            if (active is null)
            {
                CurrentStatus.Text = "현재 유효한 밴 없음";
            }
            else
            {
                CurrentStatus.Text = $"현재 밴 중 (#{active.Id}, {active.PeriodText}) — 새 밴은 기존 밴을 해제한 뒤에 낼 수 있습니다";
                CurrentStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
                BanRadio.IsEnabled = false;
            }

            var options = new List<ViolationOption> { ViolationOption.None };
            options.AddRange(violTask.Result.Select(ViolationOption.From));
            ViolationBox.ItemsSource = options;
            ViolationBox.SelectedItem =
                options.FirstOrDefault(o => o.Id is long id && id == _preselectViolationId) ??
                (_preselectViolationId is null && options.Count > 1 ? options[1] : options[0]);

            ExecuteButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"불러오기 실패: {ex.Message}";
        }

        _ready = true;
        FillReasonFromViolation();
        UpdateUi();
        ReasonBox.Focus();
    }

    private void Action_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateUi();
    }

    private void Duration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateUi();
    }

    private void ViolationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) FillReasonFromViolation();
    }

    private void ReasonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 직접 입력하면 자동 채움을 멈추고, 다 지우면 다시 자동으로
        if (!_settingReason) _reasonAuto = ReasonBox.Text.Length == 0;
    }

    private void FillReasonFromViolation()
    {
        if (!_reasonAuto) return;
        var option = ViolationBox.SelectedItem as ViolationOption;
        _settingReason = true;
        ReasonBox.Text = option?.SuggestedReason ?? "";
        ReasonBox.CaretIndex = ReasonBox.Text.Length;
        _settingReason = false;
    }

    private bool IsBan => BanRadio.IsChecked == true;

    /// <summary>선택한 기간(분). 영구면 null.</summary>
    private int? DurationMinutes =>
        int.TryParse((DurationBox.SelectedItem as ComboBoxItem)?.Tag as string, out var m) ? m : null;

    private string DurationText => (DurationBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";

    private void UpdateUi()
    {
        DurationBox.IsEnabled = IsBan;

        if (IsBan)
        {
            ExecuteButton.Content = DurationMinutes is null ? "영구 밴 실행" : $"{DurationText} 밴 실행";
            ExecuteButton.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            Notice.Text = "지금 접속 중이면 서버가 끊고, 이후 재접속을 거부합니다. " +
                          (DurationMinutes is null
                              ? "영구 밴은 해제하기 전까지 유지됩니다."
                              : "만료 시각은 DB 시계 기준으로 계산됩니다.");
        }
        else
        {
            ExecuteButton.Content = "킥 실행";
            ExecuteButton.ClearValue(ForegroundProperty);
            Notice.Text = $"접속 중이면 서버가 곧 끊습니다. {BanRepository.KickWindowSeconds}초 안에 접속 중이 아니면 " +
                          "킥은 효력 없이 만료됩니다.";
        }
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var reason = ReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            ErrorText.Text = "사유를 입력하세요.";
            ReasonBox.Focus();
            return;
        }

        var ban = IsBan;
        var minutes = ban ? DurationMinutes : null;

        if (ban && minutes is null)
        {
            var confirm = MessageBox.Show(this,
                $"{PlayerTitle.Text}\n\n영구 밴을 발행합니다. 계속할까요?",
                "영구 밴 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        ExecuteButton.IsEnabled = false;
        try
        {
            var violationId = (ViolationBox.SelectedItem as ViolationOption)?.Id;
            var id = await BanRepository.IssueAsync(_playerId, ban ? "BAN" : "KICK", reason, violationId, minutes);

            IssuedBanId = id;
            ResultSummary = ban
                ? $"밴 #{id} 발행 ({(minutes is null ? "영구" : DurationText)}) · {PlayerTitle.Text}"
                : $"킥 #{id} 발행 · {PlayerTitle.Text}";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"발행 실패: {ex.Message}";
            ExecuteButton.IsEnabled = true;
        }
    }
}
