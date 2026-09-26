using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AntiCheatDashboard.Data;

namespace AntiCheatDashboard.Views;

/// <summary>
/// 실시간 위반 탭. 폴링마다 필터 조건의 최근 N행을 통째로 다시 읽는다
/// (violations 행이 나중에 갱신될 가능성까지 안전하게 반영하기 위해 증분 조회를 쓰지 않음).
/// 새 행 판정은 필터와 무관하게 전역 MAX(id) 기준 — 필터를 바꿔도 옛 행이 새 행으로 보이지 않는다.
/// </summary>
public partial class LiveViolationsView : UserControl
{
    private const string AllText = "전체";
    private static readonly TimeSpan NewHighlight = TimeSpan.FromSeconds(10);
    private const int ListReloadEvery = 10; // 폴링 10회(약 30초)마다 매치/코드 목록 갱신

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글 그대로 표시
    };

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, DateTime> _firstSeen = new();
    private long? _baselineMaxId;      // 직전 폴링 시점의 전역 MAX(violations.id)
    private long? _selectedId;
    private List<string> _allCodes = new();
    private int _tickCount;

    private bool _initialized;         // 매치/코드 목록 최초 로드 완료
    private bool _ready;               // 이벤트 처리 허용
    private bool _suppress;            // 코드로 선택을 바꾸는 동안 이벤트 무시
    private bool _busy;
    private bool _pending;

    private string? _flash;            // 제재 발행 직후 상태줄에 잠깐 붙일 문구
    private DateTime _flashUntil;

    public LiveViolationsView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, Db.Config.PollSeconds)) };
        _timer.Tick += async (_, _) => await RefreshAsync(force: false);
    }

    // 탭을 떠나면 Unloaded, 돌아오면 Loaded가 다시 온다. 보이는 동안만 폴링.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            try
            {
                await ReloadListsAsync();
                _initialized = true;
            }
            catch (Exception ex)
            {
                StatusLine.Text = $"목록 로드 실패: {ex.Message}";
            }
        }
        _ready = true;
        _timer.Start();
        await RefreshAsync(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    // ───────── 필터 이벤트 ─────────

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress) return;
        await RefreshAsync(force: true);
    }

    private async void Filter_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;
        await RefreshAsync(force: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await ReloadListsAsync(); } catch { /* 목록 갱신 실패는 무시하고 데이터만 */ }
        await RefreshAsync(force: true);
    }

    private void SummaryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code }) return;
        // 이미 그 코드로 필터 중이면 해제, 아니면 적용 (SelectionChanged가 갱신을 부른다)
        CodeBox.SelectedItem = Equals(CodeBox.SelectedItem, code) ? AllText : code;
    }

    private void ViolationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var row = ViolationGrid.SelectedItem as ViolationRow;
        _selectedId = row?.Id;
        ShowDetail(row);
    }

    // ───────── 제재 ─────────

    // WPF DataGrid는 우클릭으로 행을 선택하지 않으므로, 메뉴를 열기 전에 그 행을 선택해 둔다
    private void ViolationGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row) row.IsSelected = true;

        SanctionMenuItem.IsEnabled = ViolationGrid.SelectedItem is ViolationRow { IsBot: false };
    }

    private void ViolationGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSanctionDialog();

    private void Sanction_Click(object sender, RoutedEventArgs e) => OpenSanctionDialog();

    private void OpenSanctionDialog()
    {
        if (ViolationGrid.SelectedItem is not ViolationRow { IsBot: false } v) return;

        var dialog = new BanDialog(v.PlayerId, v.PlayerText, v.Id) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        _flash = dialog.ResultSummary;
        _flashUntil = DateTime.Now.AddSeconds(15);
        _ = RefreshAsync(force: true);
    }

    // ───────── 갱신 ─────────

    private async Task RefreshAsync(bool force)
    {
        if (!_ready) return;
        if (!force && PauseBox.IsChecked == true) return;
        if (_busy) { _pending = true; return; }

        _busy = true;
        try
        {
            if (!force && ++_tickCount % ListReloadEvery == 0
                && !MatchBox.IsDropDownOpen && !CodeBox.IsDropDownOpen)
            {
                await ReloadListsAsync();
            }

            var filter = BuildFilter();
            var headTask = ViolationRepository.GetHeadAsync();
            var rowsTask = ViolationRepository.GetRecentAsync(filter);
            var sumTask = ViolationRepository.GetSummaryAsync(filter);
            await Task.WhenAll(headTask, rowsTask, sumTask);

            var head = headTask.Result;
            var rows = rowsTask.Result;
            var now = DateTime.Now;

            // 새 행 판정: 직전 폴링의 전역 MAX(id)보다 큰 id만 새 행
            if (_baselineMaxId is long baseline)
            {
                foreach (var r in rows.Where(r => r.Id > baseline))
                    _firstSeen.TryAdd(r.Id, now);
            }
            _baselineMaxId = head.MaxViolationId;

            foreach (var r in rows)
                r.IsNew = _firstSeen.TryGetValue(r.Id, out var seen) && now - seen < NewHighlight;

            // 오래된 강조 기록 정리
            foreach (var id in _firstSeen.Where(kv => now - kv.Value > NewHighlight).Select(kv => kv.Key).ToList())
                _firstSeen.Remove(id);

            BindRows(rows);
            BindSummary(sumTask.Result);

            var newCount = rows.Count(r => r.IsNew);
            var matchDesc = filter.LatestMatch ? $"최신 매치 #{head.LatestMatchId}"
                          : filter.MatchId is long mid ? $"매치 #{mid}"
                          : "전체 매치";
            StatusLine.Text =
                (_flash != null && now < _flashUntil ? $"{_flash} · " : "") +
                $"마지막 갱신 {now:HH:mm:ss} · {matchDesc} · {rows.Count}행 (최대 {filter.Limit})" +
                (newCount > 0 ? $" · 새 위반 {newCount}건" : "") +
                (PauseBox.IsChecked == true ? " · 일시정지 중" : $" · {Db.Config.PollSeconds}초마다 갱신");
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"{DateTime.Now:HH:mm:ss} 갱신 실패 (이전 데이터 유지): {ex.Message}";
        }
        finally
        {
            _busy = false;
        }

        if (_pending)
        {
            _pending = false;
            await RefreshAsync(force: true);
        }
    }

    private ViolationFilter BuildFilter()
    {
        var match = MatchBox.SelectedItem as MatchOption;
        var code = CodeBox.SelectedItem as string;
        var layer = (LayerBox.SelectedItem as ComboBoxItem)?.Content as string;
        var window = (WindowBox.SelectedItem as ComboBoxItem)?.Tag as string;

        return new ViolationFilter
        {
            MatchId = match?.Id,
            LatestMatch = match?.Latest ?? false,
            Code = code is null or AllText ? null : code,
            Layer = layer is null or AllText ? null : layer,
            SummaryWindowMinutes = int.TryParse(window, out var w) ? w : null,
            ApplyExclusions = ExcludeBox.IsChecked == true,
            Limit = Db.Config.MaxRows,
        };
    }

    private void BindRows(List<ViolationRow> rows)
    {
        _suppress = true;
        try
        {
            ViolationGrid.ItemsSource = rows;
            var selected = _selectedId is long sid ? rows.FirstOrDefault(r => r.Id == sid) : null;
            ViolationGrid.SelectedItem = selected;
            ShowDetail(selected);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>구간 안에 0건인 코드도 카드로 보이도록 전체 코드 목록과 합친다.</summary>
    private void BindSummary(List<CodeSummary> sums)
    {
        var byCode = sums.ToDictionary(s => s.Code);
        var cards = _allCodes.Union(byCode.Keys)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => byCode.TryGetValue(c, out var s) ? s : new CodeSummary { Code = c })
            .ToList();
        SummaryList.ItemsSource = cards;
    }

    private async Task ReloadListsAsync()
    {
        var matchesTask = ViolationRepository.GetRecentMatchesAsync();
        var codesTask = ViolationRepository.GetCodesAsync();
        await Task.WhenAll(matchesTask, codesTask);

        _suppress = true;
        try
        {
            // 매치: 기존 선택 유지, 처음엔 최신 매치
            var prevKey = (MatchBox.SelectedItem as MatchOption)?.Key ?? "latest";
            var matchOptions = new List<MatchOption> { MatchOption.All, MatchOption.LatestMatch };
            matchOptions.AddRange(matchesTask.Result);
            MatchBox.ItemsSource = matchOptions;
            MatchBox.SelectedItem = matchOptions.FirstOrDefault(o => o.Key == prevKey) ?? matchOptions[1];

            // 코드: 기존 선택 유지
            _allCodes = codesTask.Result;
            var prevCode = CodeBox.SelectedItem as string ?? AllText;
            var codeOptions = new List<string> { AllText };
            codeOptions.AddRange(_allCodes);
            CodeBox.ItemsSource = codeOptions;
            CodeBox.SelectedItem = codeOptions.Contains(prevCode) ? prevCode : AllText;
        }
        finally
        {
            _suppress = false;
        }
    }

    // ───────── 상세 ─────────

    private void ShowDetail(ViolationRow? r)
    {
        SanctionButton.IsEnabled = r is { IsBot: false };
        if (r is null)
        {
            DetailHeader.Text = "행을 선택하면 상세가 표시됩니다";
            DetailBox.Text = "";
            return;
        }

        DetailHeader.Text = $"#{r.Id} · {r.Code} ({r.Layer}, 심각도 {r.Severity})";

        var sb = new StringBuilder();
        sb.AppendLine($"플레이어    {r.PlayerText}");
        sb.AppendLine($"매치        {r.MatchText}");
        sb.AppendLine($"시각(KST)   {r.TimeKst:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"틱 구간     {r.FirstTick} → {r.LastTick}  ({r.LastTick - r.FirstTick} 틱)");
        sb.AppendLine($"발생 횟수   {r.Occurrences}");
        sb.AppendLine($"RTT         {r.RttText} ms");
        sb.AppendLine();
        sb.AppendLine("detail_json");
        sb.AppendLine(PrettyJson(r.DetailJson));
        DetailBox.Text = sb.ToString();
    }

    private static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(없음)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return json; // JSON이 아니면 원문 그대로
        }
    }
}
