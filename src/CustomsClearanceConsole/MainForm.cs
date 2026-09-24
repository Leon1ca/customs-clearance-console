using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CustomsClearanceConsole;

internal sealed partial class MainForm : Form
{
    private enum BatchState { Empty, Ready, Processing, Complete }

    private readonly StateStore _store = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 200 };
    private AppState _state;
    private int _filterIndex;
    private int _page = 1;
    private List<DeclarationRecord> _visible = [];
    private ScanSession? _session;
    private readonly Dictionary<string, BrowserValidation> _browserSessions = [];
    private readonly Dictionary<string, int> _manualWidths = new(StringComparer.Ordinal);
    private bool _applyingWidths;
    private bool _previewProcessing;
    private string _batchFolder = "";
    private int _batchFileCount;
    private string _batchFileSummary = "";
    private Responsive.Layout _layout = Responsive.Compute(1200, 720);

    private TableLayoutPanel _body = null!;
    private TableLayoutPanel _statsRow = null!;
    private TableLayoutPanel _toolbar = null!;
    private TableLayoutPanel _recordsContent = null!;
    private TitleBlock _titleBlock = null!;
    private KpiPanel _kpi = null!;
    private MoneySummaryPanel _moneySummary = null!;
    private ProgressStrip _progressStrip = null!;
    private LockBar _lockBar = null!;
    private FilterSegmented _filterSegmented = null!;
    private TextBox _search = null!;
    private SearchField _searchHost = null!;
    private Button _cleanupButton = null!;
    private Button _exportButton = null!;
    private Button _scan = null!;
    private Button _settings = null!;
    private DataGridView _grid = null!;
    private Panel _gridHost = null!;
    private RecordStatePanel _recordState = null!;
    private Label _dropBanner = null!;
    private Label _footer = null!;
    private Label _pageLabel = null!;
    private Button _previous = null!;
    private Button _next = null!;
    private ModernDropDown _pageSize = null!;
    private ToastControl _toast = null!;
    private CopyContextMenu _copyMenu = null!;
    private DesignMenu _exportMenu = null!;
    private DesignMenu _cleanupMenu = null!;

    public MainForm()
    {
        _state = _store.Load();
        if (_state.PageSize is not (50 or 100 or 200)) _state.PageSize = 50;
        _batchFolder = string.IsNullOrWhiteSpace(_state.LastFolder) ? "" : _state.LastFolder;
        if (_batchFolder.Length > 0 && _state.Records.Count == 0) LoadBatchFileSummary(_batchFolder);

        Text = "关单核验台";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1200, 720);
        Size = new Size(1440, 900);
        BackColor = Theme.Canvas;
        Font = Theme.UiFont(13.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        _exportMenu = new DesignMenu(
        [
            new DesignMenu.Entry("excel", "导出为 Excel", null, Ui2.FileExcel, ".xlsx"),
            new DesignMenu.Entry("markdown", "导出为 Markdown", null, Ui2.FileMarkdownInk, ".md")
        ]);
        _exportMenu.ItemSelected += (_, key) => ExportList(key);
        _cleanupMenu = new DesignMenu(
        [
            new DesignMenu.Entry("list", "列表清理", "仅清除识别记录，不删除文件", Ui2.TrashInk),
            new DesignMenu.Entry("docs", "关单清理", "当前层 PDF / 图片移入回收站", Ui2.TrashRed, Danger: true, SeparatorBefore: true),
            new DesignMenu.Entry("screenshots", "截图清理", "核验截图移入回收站", Ui2.TrashRed, Danger: true)
        ]);
        _cleanupMenu.ItemSelected += (_, key) =>
        {
            if (key == "list") ClearList();
            else if (key == "docs") CleanDeclarationFolder();
            else CleanScreenshotFolder();
        };

        BuildWorkspace();
        _pageSize.SelectedItem = $"{_state.PageSize} 条";
        if (_pageSize.SelectedIndex < 0) _pageSize.SelectedItem = "50 条";
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _page = 1; RefreshGrid(); };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            _searchTimer.Dispose();
            _exportMenu.Dispose();
            _cleanupMenu.Dispose();
            foreach (var session in _browserSessions.Values) _ = session.DisposeAsync();
            _browserSessions.Clear();
        };
        ApplyResponsiveLayout();
        RefreshAll();
    }

    private BatchState CurrentState => _session is not null || _previewProcessing ? BatchState.Processing
        : _state.Records.Count > 0 ? BatchState.Complete
        : _batchFolder.Length > 0 ? BatchState.Ready
        : BatchState.Empty;

    /// <summary>Native UI self-test hook: render the recognition-in-progress state without running OCR.</summary>
    internal void PreviewProcessing(int done, int total, string file, int completed, int attention, int failed)
    {
        _previewProcessing = true;
        _progressStrip.Done = done;
        _progressStrip.Total = total;
        _progressStrip.FileName = file;
        _progressStrip.Completed = completed;
        _progressStrip.Attention = attention;
        _progressStrip.Failed = failed;
        RefreshAll();
    }

    /// <summary>Native UI self-test hook: apply a search query for the "no results" state.</summary>
    internal void ApplyPreviewSearch(string text)
    {
        _search.Text = text;
        _page = 1;
        RefreshGrid();
    }

    internal void ApplyPreviewFilter(int index)
    {
        _filterIndex = index;
        _page = 1;
        RefreshGrid();
    }

    /// <summary>Native UI self-test hook: the layout applied for the current client size.</summary>
    internal Responsive.Layout AppliedLayout => _layout;

    // ---- settings / batch loading ----

    private void ShowDirectorySettings()
    {
        if (_session is not null) return;
        using var dialog = new DirectorySettingsForm(_state.LastFolder, _state.ScreenshotFolder);
        if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
        var newFolder = dialog.DeclarationFolder;
        _state.ScreenshotFolder = dialog.ScreenshotFolder;
        if (!string.Equals(newFolder, _state.LastFolder, StringComparison.OrdinalIgnoreCase))
        {
            // A different valid folder is a new batch: pre-check, then replace the batch.
            if (!Directory.Exists(newFolder))
            {
                MessageBox.Show("新的关单目录不可访问，已保留当前批次。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            InvalidateBrowserSessions();
            _state.LastFolder = newFolder;
            _state.Records.Clear();
            _page = 1;
            LoadBatchFileSummary(newFolder);
        }
        SaveState();
        RefreshAll();
    }

    private void LoadBatchFileSummary(string folder)
    {
        _batchFolder = folder;
        try
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(ScanPlan.IsSupported).ToList();
            _batchFileCount = files.Count;
            var pdf = files.Count(x => Path.GetExtension(x).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            var images = files.Count - pdf;
            _batchFileSummary = $"{folder} · {files.Count} 个文件" + (pdf > 0 || images > 0 ? $"（PDF {pdf} · 图片 {images}）" : "");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            _batchFileCount = 0;
            _batchFileSummary = folder;
        }
    }

    private async Task ScanAsync() => await PrepareAndScanAsync(() => ScanPlan.FromFolder(_state.LastFolder));

    private async Task ScanDroppedFilesAsync(IReadOnlyList<string> files) =>
        await PrepareAndScanAsync(() => ScanPlan.FromFiles(files));

    private async Task PrepareAndScanAsync(Func<ScanPlan> prepare)
    {
        if (_session is not null) return;
        ScanPlan plan;
        try { plan = prepare(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "批次未开始", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        InvalidateBrowserSessions();
        using var session = new ScanSession(plan);
        _session = session;
        _state.Records = session.Completed;
        _page = 1;
        _batchFolder = plan.Files.Count > 0 ? Path.GetDirectoryName(plan.Files[0]) ?? _batchFolder : _batchFolder;
        _progressStrip.Total = plan.Files.Count;
        _progressStrip.Done = 0;
        _progressStrip.Completed = 0;
        _progressStrip.Attention = 0;
        _progressStrip.Failed = 0;
        _progressStrip.FileName = "准备识别";
        RefreshAll();
        var progress = new InlineProgress<(int Done, int Total, string File)>(x =>
        {
            _progressStrip.Done = x.Done;
            _progressStrip.Total = x.Total;
            _progressStrip.FileName = x.File;
            _progressStrip.Invalidate();
        });
        var items = new InlineProgress<DeclarationRecord>(record =>
        {
            session.Completed.Add(record);
            BatchScanner.MarkDuplicates(session.Completed);
            _progressStrip.Completed = session.Completed.Count(x => !x.NeedsAttention && !x.IsDuplicate);
            _progressStrip.Attention = session.Completed.Count(x => x.NeedsAttention);
            _progressStrip.Failed = session.Completed.Count(x => x.Status == "识别失败");
            RefreshGrid();
        });
        try { await new BatchScanner().ScanAsync(plan, progress, session.Token, items); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            var cancelled = session.Token.IsCancellationRequested;
            BatchScanner.MarkDuplicates(session.Completed);
            _state.Records = BatchScanner.SortRecords(session.Completed).ToList();
            _session = null;
            LoadBatchFileSummary(_batchFolder.Length > 0 ? _batchFolder : _state.LastFolder);
            SaveState();
            RefreshAll();
            if (cancelled && _state.Records.Count > 0)
                ShowToast($"已取消，保留 {_state.Records.Count} 条已完成结果");
        }
    }

    // ---- drag & drop pre-check ----

    private static string[] DroppedSupportedFiles(DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return [];
        var extensions = new HashSet<string>(BatchScanner.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        return ((string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [])
            .Where(File.Exists).Where(x => extensions.Contains(Path.GetExtension(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void GridDragEnter(object? sender, DragEventArgs e) => e.Effect = DroppedSupportedFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private void GridDragOver(object? sender, DragEventArgs e)
    {
        if (_session is not null) { e.Effect = DragDropEffects.None; return; }
        var files = DroppedSupportedFiles(e);
        var message = files.Length == 0 ? "未找到可识别的关单文件"
            : files.Length > BatchScanner.MaximumFiles ? $"本批 {files.Length} 个文件，超过每批 {BatchScanner.MaximumFiles} 个上限，整批停止"
            : $"可载入 {files.Length} 个文件，松开后作为新批次识别";
        ShowDropFeedback(message, files.Length > 0 && files.Length <= BatchScanner.MaximumFiles);
        e.Effect = files.Length > 0 && files.Length <= BatchScanner.MaximumFiles ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void GridDragDrop(object? sender, DragEventArgs e)
    {
        HideDropFeedback();
        var files = DroppedSupportedFiles(e);
        if (files.Length == 0) return;
        if (files.Length > BatchScanner.MaximumFiles)
        {
            MessageBox.Show($"一次最多拖入 {BatchScanner.MaximumFiles} 个关单文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await ScanDroppedFilesAsync(files);
    }

    private void ShowDropFeedback(string message, bool allowed)
    {
        _dropBanner.Text = message;
        _dropBanner.ForeColor = allowed ? Theme.Primary : Theme.Warning;
        _dropBanner.BackColor = allowed ? Color.FromArgb(240, 246, 253) : UiTokens.Status.AttentionNotice;
        _dropBanner.Visible = true;
        _dropBanner.BringToFront();
    }

    private void HideDropFeedback() => _dropBanner.Visible = false;

    // ---- filtering / paging ----

    private IEnumerable<DeclarationRecord> FilteredRecords()
    {
        var query = _state.Records.AsEnumerable();
        var search = _search.Text.Trim();
        if (search.Length > 0)
            query = query.Where(x => new[] { x.DeclarationNo, x.SourceName, x.Consignee, x.ContractNo }
                .Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        query = _filterIndex switch
        {
            1 => query.Where(x => !x.IsDuplicate && !x.NeedsAttention),
            2 => query.Where(x => x.IsDuplicate),
            3 => query.Where(x => x.NeedsAttention),
            _ => query
        };
        return BatchScanner.SortRecords(query);
    }

    private int CurrentPageSize()
    {
        var text = _pageSize.SelectedItem?.ToString() ?? "50";
        return int.TryParse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var size) && size is 50 or 100 or 200 ? size : 50;
    }

    private int PageCount() => Math.Max(1, (int)Math.Ceiling(_visible.Count / (double)CurrentPageSize()));

    // ---- refresh ----

    private void RefreshAll()
    {
        var state = CurrentState;
        var processing = state == BatchState.Processing;
        _body.RowStyles[2].Height = processing ? UiScale.Px(this, _layout.CompactHeight ? 92 : 104) : 0;
        _body.RowStyles[3].Height = processing ? UiScale.Px(this, _layout.CompactHeight ? 34 : 38) : 0;
        _exportButton.Enabled = _state.Records.Count > 0 && !processing;
        _exportButton.Image?.Dispose();
        _exportButton.Image = UiV2Icons.Load(_exportButton.Enabled ? Ui2.Export : Ui2.ExportDisabled, 16, DeviceDpi);
        _settings.Enabled = !processing;
        _cleanupButton.Enabled = !processing;
        _filterSegmented.Enabled = _state.Records.Count > 0 && !processing;
        _searchHost.Enabled = _state.Records.Count > 0 && !processing;
        if (processing)
        {
            _scan.Text = "取消识别";
            _scan.Image?.Dispose();
            _scan.Image = UiV2Icons.Load(Ui2.Stop, 16, DeviceDpi);
            _scan.BackColor = Color.White;
            _scan.ForeColor = Theme.Danger;
            _scan.AccessibleName = "取消识别";
            if (_scan is RoundedButton cancelRounded)
            {
                cancelRounded.BorderColor = ColorTranslator.FromHtml("#E4A6A0");
                cancelRounded.HoverBackColor = UiTokens.Status.AttentionNotice;
                cancelRounded.Invalidate();
            }
        }
        else
        {
            _scan.Text = "开始识别";
            _scan.Image?.Dispose();
            _scan.Image = UiV2Icons.Load(Ui2.Play, 16, DeviceDpi);
            var primaryFill = state == BatchState.Empty ? UiTokens.Colors.DisabledPrimaryFill : Theme.Primary;
            _scan.BackColor = primaryFill;
            _scan.ForeColor = Color.White;
            _scan.Enabled = state is BatchState.Ready or BatchState.Complete || _state.Records.Count > 0;
            _scan.AccessibleName = "开始识别";
            if (_scan is RoundedButton startRounded)
            {
                startRounded.BorderColor = primaryFill;
                startRounded.HoverBackColor = Theme.HeaderBg;
                startRounded.Invalidate();
            }
        }
        RefreshGrid();
    }

    private void RefreshGrid()
    {
        _visible = FilteredRecords().ToList();
        _page = Math.Clamp(_page, 1, PageCount());
        var size = CurrentPageSize();
        var items = _visible.Skip((_page - 1) * size).Take(size).ToList();
        _grid.Rows.Clear();
        var rowHeight = UiScale.Px(this, _layout.TableRow);
        for (var i = 0; i < items.Count; i++)
        {
            var record = items[i];
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];
            row.Height = rowHeight;
            row.Tag = record;
            row.Cells["Index"].Value = ((_page - 1) * size + i + 1).ToString("D2");
            row.Cells["Status"].Value = StatusLabel(record);
            row.Cells["No"].Value = CopyNumberText(record);
            row.Cells["Consignee"].Value = Dash(record.Consignee);
            row.Cells["Contract"].Value = Dash(record.ContractNo);
            row.Cells["Port"].Value = Dash(record.ExitCustoms);
            row.Cells["Dest"].Value = Dash(record.DestinationCountry);
            row.Cells["PortDest"].Value = Dash(record.ExitCustoms) + "\n" + Dash(record.DestinationCountry);
            row.Cells["Amount"].Value = CopyAmountText(record);
            row.Cells["Detail"].Value = "明细";
            row.Cells["Verify"].Value = record.HasScreenshot ? "已留存" : record.HasValidDeclarationNo ? "校验" : "不可核验";
            foreach (DataGridViewCell cell in row.Cells)
            {
                cell.ToolTipText = Convert.ToString(cell.FormattedValue) ?? "";
                cell.Style.BackColor = record.IsDuplicate ? UiTokens.Status.Duplicate.Row : record.NeedsAttention ? UiTokens.Status.Attention.Row : Theme.Surface;
                cell.Style.SelectionBackColor = UiTokens.Status.CellSelected;
                cell.Style.SelectionForeColor = Theme.Text;
            }
            row.Cells["Index"].ToolTipText = "行号（本页内序号）";
            row.Cells["Status"].ToolTipText = StatusLabel(record) + (record.AllWarnings.Length > 0 ? "\n" + record.AllWarnings : "");
            row.Cells["No"].ToolTipText = CopyNumberText(record);
            row.Cells["Amount"].ToolTipText = CopyAmountText(record);
            row.Cells["Consignee"].ToolTipText = Dash(record.Consignee);
            row.Cells["Contract"].ToolTipText = Dash(record.ContractNo);
            row.Cells["Port"].ToolTipText = Dash(record.ExitCustoms);
            row.Cells["Dest"].ToolTipText = Dash(record.DestinationCountry);
            row.Cells["PortDest"].ToolTipText = Dash(record.ExitCustoms) + "\n" + Dash(record.DestinationCountry);
            row.Cells["Detail"].ToolTipText = "打开只读关单明细";
            row.Cells["Verify"].ToolTipText = !record.HasValidDeclarationNo ? "未识别出有效报关单号，无法在线核验"
                : record.HasScreenshot ? "已留存；点击可重新核验并保存新截图"
                : "打开单一窗口核验页面；在网页悬浮按钮上手动长截图后回填";
        }
        UpdateSummary(items.Count);
    }

    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void UpdateSummary(int visibleCount)
    {
        var state = CurrentState;
        var records = _state.Records;
        var duplicateGroups = records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count();
        var duplicateFiles = records.Count(x => x.IsDuplicate);
        var attention = records.Count(x => x.NeedsAttention);
        var savedNumbers = records.Where(x => x.HasScreenshot).Select(x => x.DeclarationNo).Where(x => x.Length > 0).Distinct().Count();
        var validNumbers = records.Where(x => x.HasValidDeclarationNo).Select(x => x.DeclarationNo).Distinct().Count();
        var placeholder = state == BatchState.Empty;

        _kpi.Set(
        [
            new KpiCell("本批文件", records.Count.ToString(), "份",
                state switch { BatchState.Empty => "尚未载入", BatchState.Ready => "待识别", BatchState.Processing => "识别中", _ => $"识别完成 {records.Count(x => x.Status is "识别完成" or "OCR 识别完成" or "双引擎校验通过")} / {records.Count}" },
                state == BatchState.Empty ? KpiTone.Placeholder : KpiTone.Normal),
            new KpiCell("重复单号", duplicateGroups.ToString(), "组",
                placeholder ? "尚未载入" : records.Count == 0 ? "识别后统计" : $"{duplicateFiles} 份文件，合计只计 1 份",
                duplicateGroups > 0 ? KpiTone.Danger : placeholder ? KpiTone.Placeholder : KpiTone.Normal),
            new KpiCell("需关注", attention.ToString(), "",
                placeholder ? "尚未载入" : records.Count == 0 ? "识别后统计" : records.Any(x => x.Totals.Count == 0) || records.Any(x => x.LineTotals.Any(l => !l.IsReliable)) ? "金额双引擎不一致" : "无异常",
                attention > 0 ? KpiTone.Warning : placeholder ? KpiTone.Placeholder : KpiTone.Normal),
            new KpiCell("截图留存", $"{savedNumbers}", validNumbers > 0 ? $"/ {validNumbers} 单号" : "单号",
                placeholder ? "尚未载入" : records.Count == 0 ? "识别后统计" : "网页悬浮按钮截图后自动回填",
                savedNumbers > 0 ? KpiTone.Accent : placeholder ? KpiTone.Placeholder : KpiTone.Normal)
        ]);

        var gross = BatchScanner.GrossTotals(records);
        var net = BatchScanner.DeduplicatedTotals(records);
        var rows = new List<MoneySummaryRow>();
        foreach (var currency in gross.Keys.Union(net.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
        {
            var before = records.Count(x => x.Totals.ContainsKey(currency));
            var after = records.Count(x => x.IsCanonical && x.Totals.ContainsKey(currency));
            rows.Add(new MoneySummaryRow(currency, before, after, gross.GetValueOrDefault(currency), net.GetValueOrDefault(currency)));
        }
        // Any record with an unreliable line has unconfirmed money, not only records whose
        // whole Totals map is empty. Amounts stay grouped by currency and are never summed
        // across currencies under a single label.
        var unconfirmedRows = records
            .SelectMany(record => record.LineTotals.Where(line => !line.IsReliable)
                .GroupBy(line => string.IsNullOrWhiteSpace(line.Currency) ? "—" : line.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Record: record, Currency: group.Key, Amount: group.Sum(line => line.Amount), Count: group.Count())))
            .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new UnconfirmedRow(group.Key, group.Sum(x => x.Amount), group.Sum(x => x.Count), group.First().Record.SourceName))
            .ToList();
        _moneySummary.Set(new MoneySummarySnapshot(rows, unconfirmedRows));
        _body.RowStyles[1].Height = UiScale.Px(this, Math.Max(126, Math.Min(280,
            96 + rows.Count * (_layout.AmountRow + 4) + (unconfirmedRows.Count > 0 ? 26 + unconfirmedRows.Count * 26 : 0))));

        var counts = new[] { records.Count, records.Count(x => !x.IsDuplicate && !x.NeedsAttention), records.Count(x => x.IsDuplicate), attention };
        _filterSegmented.Set(
        [
            new FilterSegment("全部", "全部记录", counts[0], records.Count > 0, _filterIndex == 0),
            new FilterSegment("正常", "正常记录", counts[1], records.Count > 0, _filterIndex == 1),
            new FilterSegment("重复", "重复记录", counts[2], records.Count > 0, _filterIndex == 2),
            new FilterSegment("需关注", "需关注记录", counts[3], records.Count > 0, _filterIndex == 3)
        ]);

        _dropBanner ??= new Label { Visible = false, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.UiFont(13F), Height = 34 };
        var statePanelVisible = visibleCount == 0;
        if (state == BatchState.Processing && visibleCount == 0)
            _recordState.Set(new StateMessage("正在识别，请稍候…", "结果会逐条写入列表；取消后保留已完成结果", null, null, null), false);
        else if (state == BatchState.Empty)
            _recordState.Set(new StateMessage("尚未载入关单", "在设置中选择关单目录，或将 PDF / 图片拖入此处", Ui2.FolderUpload, "选择关单目录", null), true);
        else if (state == BatchState.Ready && records.Count == 0)
            _recordState.Set(new StateMessage($"已载入 {_batchFileCount} 个文件", "点击“开始识别”后在此列出识别结果", null, null, _batchFileSummary), false);
        else if (records.Count > 0 && visibleCount == 0)
            _recordState.Set(new StateMessage("没有匹配的记录", "调整筛选或搜索条件；汇总仍按本批全部记录计算", null, null, null), false);
        _recordState.Visible = statePanelVisible;
        if (_recordState.Visible)
        {
            LayoutRecordState();
            _recordState.BringToFront();
        }

        _footer.Text = records.Count == 0 ? "共 0 条"
            : _visible.Count != records.Count ? $"显示 {_visible.Count} 条，共 {records.Count} 条 · 重复记录置顶"
            : $"显示 {_visible.Count} 条，共 {records.Count} 条 · 重复记录置顶";
        _pageLabel.Text = $"{_page}";
        _previous.Enabled = _page > 1;
        _next.Enabled = _page < PageCount();
        if (_pageSize.SelectedIndex < 0) _pageSize.SelectedItem = "50 条";
    }

    private void LayoutRecordState()
    {
        if (_gridHost is null || _recordState is null || _grid is null) return;
        if (_gridHost.ClientSize.Width <= 0) return;
        var header = _grid.ColumnHeadersHeight;
        _recordState.Bounds = new Rectangle(0, header, _gridHost.ClientSize.Width, Math.Max(0, _gridHost.ClientSize.Height - header));
        if (_dropBanner is null) return;
        _dropBanner.Bounds = new Rectangle(Math.Max(0, (_gridHost.ClientSize.Width - 420) / 2), 6, Math.Min(420, _gridHost.ClientSize.Width), 34);
        if (_dropBanner.Parent is null)
        {
            _gridHost.Controls.Add(_dropBanner);
            _dropBanner.BringToFront();
        }
    }

    // ---- grid painting ----

    private void PaintRecordCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record) return;
        var name = _grid.Columns[e.ColumnIndex].Name;
        if (name is not ("Index" or "Status" or "No" or "PortDest" or "Amount" or "Detail" or "Verify")) return;
        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
        var graphics = e.Graphics!;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var dpi = DeviceDpi;
        int S(int px) => (int)Math.Round(px * dpi / 96.0);
        var bounds = e.CellBounds;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        if (selected)
        {
            using var selection = new SolidBrush(UiTokens.Status.CellSelected);
            graphics.FillRectangle(selection, bounds);
        }
        switch (name)
        {
            case "Index":
                using (var font = Theme.MonoFont(12F))
                    TextRenderer.DrawText(graphics, Convert.ToString(e.Value) ?? "", font, bounds, Theme.Placeholder,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;
            case "Status":
                DrawStatusTag(graphics, bounds, record);
                break;
            case "No":
                DrawNumberCell(graphics, bounds, record, S);
                break;
            case "PortDest":
                using (var portFont = Theme.UiFont(13.5F))
                    TextRenderer.DrawText(graphics, Dash(record.ExitCustoms), portFont, new Rectangle(bounds.X + S(8), bounds.Y + S(6), bounds.Width - S(16), S(20)), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                using (var destFont = Theme.UiFont(12F))
                    TextRenderer.DrawText(graphics, Dash(record.DestinationCountry), destFont, new Rectangle(bounds.X + S(8), bounds.Y + S(26), bounds.Width - S(16), S(18)), Theme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                break;
            case "Amount":
                DrawAmountCell(graphics, bounds, record, S);
                break;
            case "Detail":
                DrawActionButton(graphics, bounds, "明细", Theme.DetailButtonBg, Theme.Primary, Theme.Border, S, enabled: true);
                break;
            case "Verify":
                if (record.HasScreenshot) DrawKeptTag(graphics, bounds, S);
                else if (!record.HasValidDeclarationNo)
                    TextRenderer.DrawText(graphics, "不可核验", Theme.UiFont(12.5F), bounds, Theme.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                else DrawVerifyButton(graphics, bounds, S);
                break;
        }
        e.Handled = true;
    }

    internal static string StatusLabel(DeclarationRecord record) =>
        record.Status == "识别失败" ? "识别失败"
        : record.IsDuplicate ? "重复"
        : record.NeedsAttention ? "需关注"
        : "正常";

    /// <summary>Full text copied from the number cell: declaration number plus source file.</summary>
    internal static string CopyNumberText(DeclarationRecord record) =>
        (record.DeclarationNo.Length == 0 ? "未识别" : record.DeclarationNo) + "\n源文件：" + record.SourceName;

    /// <summary>
    /// Per-currency amounts for one record. Reliable and unconfirmed sums are kept on
    /// separate lines and never combined across currencies.
    /// </summary>
    internal static IReadOnlyList<(string Currency, decimal Amount, bool Reliable)> AmountLines(DeclarationRecord record)
    {
        var lines = new List<(string, decimal, bool)>();
        foreach (var total in record.Totals.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add((total.Key, total.Value, true));
        foreach (var group in record.LineTotals.Where(x => !x.IsReliable)
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Currency) ? "—" : x.Currency, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add((group.Key, group.Sum(x => x.Amount), false));
        return lines;
    }

    /// <summary>Complete multi-currency text used for copying and tooltips.</summary>
    internal static string CopyAmountText(DeclarationRecord record)
    {
        var lines = AmountLines(record);
        if (lines.Count == 0) return "—";
        return string.Join("\n", lines.Select(x => x.Reliable
            ? $"{x.Currency} {x.Amount:N2}"
            : $"{x.Currency} {x.Amount:N2}（未确认，未计入确认合计）"));
    }

    private void DrawStatusTag(Graphics graphics, Rectangle bounds, DeclarationRecord record)
    {
        var dpi = DeviceDpi;
        int S(int px) => (int)Math.Round(px * dpi / 96.0);
        var label = StatusLabel(record);
        var palette = label == "识别失败" ? UiTokens.Status.Failed
            : label == "重复" ? UiTokens.Status.Duplicate
            : label == "需关注" ? UiTokens.Status.Attention
            : UiTokens.Status.Ok;
        using var font = Theme.UiFont(12F, FontStyle.Bold);
        var textWidth = TextRenderer.MeasureText(graphics, label, font, new Size(int.MaxValue, S(22)), TextFormatFlags.NoPadding).Width;
        var tag = new Rectangle(bounds.X + S(8), bounds.Y + (bounds.Height - S(22)) / 2, Math.Min(bounds.Width - S(16), textWidth + S(16)), S(22));
        using var path = Theme.RoundedPath(tag, S(4));
        using (var fill = new SolidBrush(palette.Bg)) graphics.FillPath(fill, path);
        TextRenderer.DrawText(graphics, label, font, tag, palette.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawNumberCell(Graphics graphics, Rectangle bounds, DeclarationRecord record, Func<int, int> S)
    {
        var number = record.DeclarationNo.Length == 0 ? "未识别" : record.DeclarationNo;
        using (var numberFont = Theme.MonoFont(13F, true))
            TextRenderer.DrawText(graphics, number, numberFont, new Rectangle(bounds.X + S(10), bounds.Y + S(9), bounds.Width - S(20), S(20)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        var fileX = bounds.X + S(10);
        if (record.IsDuplicate)
        {
            var label = record.IsCanonical ? "合计代表" : "不计入";
            using var microFont = Theme.UiFont(11F);
            var width = TextRenderer.MeasureText(graphics, label, microFont, new Size(int.MaxValue, S(16)), TextFormatFlags.NoPadding).Width + S(10);
            var tag = new Rectangle(fileX, bounds.Y + S(31), width, S(16));
            using var path = Theme.RoundedPath(tag, S(3));
            using var fill = new SolidBrush(record.IsCanonical ? Theme.AccentSoft : Theme.SegmentBg);
            graphics.FillPath(fill, path);
            TextRenderer.DrawText(graphics, label, microFont, tag, record.IsCanonical ? Theme.Accent : ColorTranslator.FromHtml("#4A5566"),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            fileX = tag.Right + S(6);
        }
        using (var fileFont = Theme.UiFont(12F))
            TextRenderer.DrawText(graphics, record.SourceName, fileFont, new Rectangle(fileX, bounds.Y + S(30), Math.Max(0, bounds.Right - fileX - S(10)), S(18)), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private void DrawAmountCell(Graphics graphics, Rectangle bounds, DeclarationRecord record, Func<int, int> S)
    {
        var lines = AmountLines(record);
        if (lines.Count == 0)
        {
            using var dashFont = Theme.UiFont(13.5F);
            TextRenderer.DrawText(graphics, "—", dashFont, bounds, Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        // Every currency gets its own labelled line; overflow shows a count and the full
        // list stays available in the tooltip and the scrolled money summary.
        var visible = lines.Take(2).ToList();
        var lineHeight = visible.Count == 1 ? bounds.Height : S(24);
        var overflow = lines.Count > 2 ? S(14) : 0;
        var startY = bounds.Y + Math.Max(0, (bounds.Height - visible.Count * lineHeight - overflow) / 2);
        using var currencyFont = Theme.UiFont(11.5F);
        for (var i = 0; i < visible.Count; i++)
        {
            var (currency, amount, reliable) = visible[i];
            var lineY = startY + i * lineHeight;
            var currencyWidth = TextRenderer.MeasureText(graphics, currency, currencyFont, new Size(int.MaxValue, S(20)), TextFormatFlags.NoPadding).Width;
            using (var amountFont = Theme.MonoFont(13.5F, true))
                TextRenderer.DrawText(graphics, amount.ToString("N2"), amountFont, new Rectangle(bounds.X, lineY, Math.Max(S(10), bounds.Width - S(10) - currencyWidth - S(6)), lineHeight),
                    reliable ? Theme.Text : Theme.Warning, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, currency, currencyFont, new Rectangle(bounds.Right - S(10) - currencyWidth, lineY, currencyWidth, lineHeight), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (!reliable)
            {
                var underlineY = lineY + lineHeight / 2 + S(8);
                using var underline = new Pen(UiTokens.Status.AttentionUnderline, S(1)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                graphics.DrawLine(underline, bounds.Right - S(10) - currencyWidth - S(64), underlineY, bounds.Right - S(10), underlineY);
            }
        }
        if (overflow > 0)
        {
            using var moreFont = Theme.UiFont(10.5F);
            TextRenderer.DrawText(graphics, $"+{lines.Count - 2} 币种 · 悬停查看全部", moreFont, new Rectangle(bounds.X, bounds.Bottom - overflow, bounds.Width - S(10), overflow), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawActionButton(Graphics graphics, Rectangle bounds, string text, Color back, Color fore, Color border, Func<int, int> S, bool enabled)
    {
        using var font = Theme.UiFont(12.5F);
        var width = Math.Min(bounds.Width - S(12), Math.Max(S(52), TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, S(28)), TextFormatFlags.NoPadding).Width + S(20)));
        var button = new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - S(28)) / 2, width, S(28));
        using var path = Theme.RoundedPath(button, S(5));
        using (var fill = new SolidBrush(enabled ? back : Theme.DisabledFill)) graphics.FillPath(fill, path);
        if (enabled)
        {
            using var pen = new Pen(border);
            graphics.DrawPath(pen, path);
        }
        TextRenderer.DrawText(graphics, text, font, button, enabled ? fore : Theme.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawVerifyButton(Graphics graphics, Rectangle bounds, Func<int, int> S)
    {
        using var font = Theme.UiFont(12.5F);
        var textWidth = TextRenderer.MeasureText(graphics, "校验", font, new Size(int.MaxValue, S(28)), TextFormatFlags.NoPadding).Width;
        var width = Math.Min(bounds.Width - S(12), textWidth + S(42));
        var button = new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - S(28)) / 2, width, S(28));
        using (var path = Theme.RoundedPath(button, S(5)))
        {
            using var fill = new SolidBrush(Color.White);
            graphics.FillPath(fill, path);
            using var pen = new Pen(UiTokens.Colors.BorderAccentSoft);
            graphics.DrawPath(pen, path);
        }
        var icon = UiV2Icons.Load(Ui2.External, 16, DeviceDpi);
        var iconWidth = icon?.Width ?? 0;
        var contentWidth = textWidth + (iconWidth > 0 ? S(6) + iconWidth : 0);
        var contentX = button.X + (button.Width - contentWidth) / 2;
        TextRenderer.DrawText(graphics, "校验", font, new Rectangle(contentX, button.Y, textWidth + S(2), button.Height), Theme.Primary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (icon is not null)
        {
            graphics.DrawImage(icon, contentX + textWidth + S(6), button.Y + (button.Height - icon.Height) / 2, icon.Width, icon.Height);
            icon.Dispose();
        }
    }

    private void DrawKeptTag(Graphics graphics, Rectangle bounds, Func<int, int> S)
    {
        using var font = Theme.UiFont(12.5F);
        var textWidth = TextRenderer.MeasureText(graphics, "已留存", font, new Size(int.MaxValue, S(26)), TextFormatFlags.NoPadding).Width;
        var width = Math.Min(bounds.Width - S(12), textWidth + S(40));
        var tag = new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - S(26)) / 2, width, S(26));
        using var path = Theme.RoundedPath(tag, S(4));
        using (var fill = new SolidBrush(UiTokens.Status.Kept.Bg)) graphics.FillPath(fill, path);
        var icon = UiV2Icons.Load(Ui2.CameraBlue, 16, DeviceDpi);
        var iconWidth = icon?.Width ?? 0;
        var contentWidth = textWidth + (iconWidth > 0 ? S(5) + iconWidth : 0);
        var contentX = tag.X + (tag.Width - contentWidth) / 2;
        TextRenderer.DrawText(graphics, "已留存", font, new Rectangle(contentX, tag.Y, textWidth + S(2), tag.Height), UiTokens.Status.Kept.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (icon is not null)
        {
            graphics.DrawImage(icon, contentX + textWidth + S(5), tag.Y + (tag.Height - icon.Height) / 2, icon.Width, icon.Height);
            icon.Dispose();
        }
    }

    // ---- grid interactions ----

    private void SelectCellForContextMenu(DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var cell = _grid[e.ColumnIndex, e.RowIndex];
        if (cell.Selected) return;
        _grid.ClearSelection();
        cell.Selected = true;
        _grid.CurrentCell = cell;
    }

    /// <summary>
    /// Extracts the current selection as tab/newline separated text. Reads the real
    /// cell values (no CellFormatting blanks) so No/Amount/Status copy with full text.
    /// </summary>
    internal (int Count, string Text) ExtractCopySelection()
    {
        var selected = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(x => x.RowIndex >= 0 && x.ColumnIndex >= 0 && x.Visible)
            .Select(x => (x.RowIndex, x.ColumnIndex, Convert.ToString(x.Value) ?? ""))
            .ToList();
        return (selected.Count, selected.Count == 0 ? "" : FormatCellSelection(selected));
    }

    private void CopySelectedCells()
    {
        var (count, text) = ExtractCopySelection();
        if (count == 0) return;
        try
        {
            Clipboard.SetText(text);
            ShowToast($"已复制 {count} 个单元格 · 制表符分隔，可直接粘贴到 Excel");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show("复制失败，请稍后重试。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static string FormatCellSelection(IEnumerable<(int Row, int Column, string Value)> selected) =>
        string.Join(Environment.NewLine, selected.OrderBy(x => x.Row).ThenBy(x => x.Column)
            .GroupBy(x => x.Row)
            .Select(row => string.Join('\t', row.Select(cell => cell.Value.Replace("\r", " ").Replace("\n", " ")))));

    private void GridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) { CopySelectedCells(); e.Handled = true; e.SuppressKeyPress = true; }
        else if ((e.Shift && e.KeyCode == Keys.F10) || e.KeyCode == Keys.Apps)
        {
            var cell = _grid.CurrentCell;
            var point = cell is null ? new Point(20, 20) : new Point(
                _grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true).Left + 16,
                _grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true).Bottom - 4);
            _copyMenu.ShowAt(_grid.PointToScreen(point), _grid.SelectedCells.Count > 0);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private async void GridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record) return;
        var column = _grid.Columns[e.ColumnIndex].Name;
        if (column == "Detail") { ShowDetail(record); return; }
        if (column != "Verify" || _session is not null) return;
        if (!record.HasValidDeclarationNo) { ShowToast("未识别出有效报关单号，无法在线核验"); return; }
        await StartVerificationAsync(record);
    }

    private void ShowDetail(DeclarationRecord record)
    {
        using var dialog = new DetailForm(record);
        ModalPresenter.Show(dialog, this);
    }

    private void GridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var column = _grid.Columns[e.ColumnIndex].Name;
        if (column is "Detail" or "Verify") return;
        if (_grid.Rows[e.RowIndex].Tag is not DeclarationRecord record || !File.Exists(record.SourcePath)) return;
        try { Process.Start(new ProcessStartInfo(record.SourcePath) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    private void ShowToast(string message) => _toast.Show(message);

    // ---- browser verification ----

    private async Task StartVerificationAsync(DeclarationRecord record)
    {
        if (!Directory.Exists(_state.ScreenshotFolder))
        {
            ShowDirectorySettings();
            if (!Directory.Exists(_state.ScreenshotFolder))
            {
                MessageBox.Show("请先在设置中选择有效的截图保存目录。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        BrowserValidation session;
        try { session = new BrowserValidation(record.DeclarationNo, _state.ScreenshotFolder); }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _browserSessions[session.SessionId] = session;
        session.CaptureCompleted += OnBrowserCaptureCompleted;
        session.StatusChanged += OnBrowserStatusChanged;
        ShowToast($"正在打开核验页面：{record.DeclarationNo}");
        try
        {
            var message = await session.StartAsync(CancellationToken.None);
            ShowToast(message);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            session.CaptureCompleted -= OnBrowserCaptureCompleted;
            session.StatusChanged -= OnBrowserStatusChanged;
            _browserSessions.Remove(session.SessionId);
            await session.DisposeAsync();
            MessageBox.Show($"无法启动核验浏览器：{ex.Message}\n\n请确认已安装 Microsoft Edge 或 Google Chrome。", "网页核验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnBrowserStatusChanged(object? sender, string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => OnBrowserStatusChanged(sender, message))); return; }
        ShowToast(message);
    }

    private void OnBrowserCaptureCompleted(object? sender, BrowserCaptureResult result)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => OnBrowserCaptureCompleted(sender, result))); return; }
        // A queued completion from an invalidated session (batch switch, list clear or
        // session disposal) must never touch the current batch, even if the declaration
        // number matches a record in the new batch.
        if (sender is not BrowserValidation session ||
            !_browserSessions.TryGetValue(session.SessionId, out var active) ||
            !ReferenceEquals(active, session) ||
            !string.Equals(session.DeclarationNo, result.DeclarationNo, StringComparison.Ordinal))
        {
            AppLog.Write($"已忽略已失效核验会话的回填：{result.DeclarationNo} · {result.State}");
            return;
        }
        switch (result.State)
        {
            case "saved" when result.FilePath is not null:
                foreach (var matching in _state.Records.Where(x => x.DeclarationNo == result.DeclarationNo))
                    matching.ScreenshotPath = result.FilePath;
                SaveState();
                RefreshGrid();
                ShowFileSavedToast(result.FilePath, "核验长截图已保存，并回填本批同单号记录");
                break;
            case "mismatch":
                ShowToast("网页结果单号与本次核验不一致，未保存截图。");
                break;
            case "too-long":
                ShowToast("页面超过 6000 万像素，未保存截图；请使用浏览器分段保存。");
                break;
            case "cancelled":
                break;
            default:
                ShowToast(result.Message);
                break;
        }
    }

    private void InvalidateBrowserSessions()
    {
        if (_browserSessions.Count == 0) return;
        foreach (var session in _browserSessions.Values.ToList())
        {
            session.CaptureCompleted -= OnBrowserCaptureCompleted;
            session.StatusChanged -= OnBrowserStatusChanged;
            _ = session.DisposeAsync();
        }
        _browserSessions.Clear();
    }

    // ---- export ----

    private void ShowExportMenu()
    {
        if (_session is not null || _state.Records.Count == 0) return;
        _exportMenu.Show(_exportButton, _exportButton.Height + 6, true);
    }

    private void ExportList(string kind)
    {
        if (_session is not null || _state.Records.Count == 0) return;
        var excel = kind == "excel";
        using var dialog = new SaveFileDialog
        {
            Title = excel ? "导出本批全部关单为 Excel" : "导出本批全部关单为 Markdown",
            Filter = excel ? "Excel 工作簿 (*.xlsx)|*.xlsx" : "Markdown 文档 (*.md)|*.md",
            DefaultExt = excel ? "xlsx" : "md",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"关单列表_{DateTime.Now:yyyyMMdd_HHmm}.{(excel ? "xlsx" : "md")}",
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var snapshot = BatchScanner.SortRecords(_state.Records).ToList();
            var now = DateTime.Now;
            if (excel) ExcelListExporter.Save(dialog.FileName, snapshot, now);
            else MarkdownListExporter.Save(dialog.FileName, snapshot, now);
            ShowFileSavedToast(dialog.FileName, excel ? "关单列表已导出为 Excel" : "关单列表已导出为 Markdown");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show($"导出未完成，请检查保存位置权限或文件占用情况。\n{ex.Message}", "列表导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowFileSavedToast(string filePath, string message)
    {
        var dpi = DeviceDpi;
        int S(int px) => (int)Math.Round(px * dpi / 96.0);
        var toast = new RoundedPanel
        {
            Size = new Size(S(392), S(70)),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = ColorTranslator.FromHtml("#F1FBF5"),
            BorderColor = ColorTranslator.FromHtml("#9ECBB0"),
            Radius = 9,
            AccessibleName = "文件保存成功提示"
        };
        toast.Location = new Point(Math.Max(S(20), ClientSize.Width - toast.Width - S(30)), Math.Max(S(20), ClientSize.Height - toast.Height - S(30)));
        var text = new Label
        {
            Text = message,
            Location = new Point(S(18), 0),
            Size = new Size(S(225), toast.Height),
            Font = Theme.UiFont(14F),
            ForeColor = Theme.Success,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var open = Theme.SecondaryButton("打开位置");
        open.Location = new Point(S(268), S(15));
        open.Size = new Size(S(106), S(40));
        open.Font = Theme.UiFont(14F);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Write(ex); }
            Controls.Remove(toast);
            toast.Dispose();
        };
        toast.Controls.AddRange([text, open]);
        Controls.Add(toast);
        toast.BringToFront();
        var timer = new System.Windows.Forms.Timer { Interval = 6000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!toast.IsDisposed) { Controls.Remove(toast); toast.Dispose(); }
            timer.Dispose();
        };
        toast.Disposed += (_, _) => { timer.Stop(); timer.Dispose(); };
        timer.Start();
    }

    // ---- cleanup ----

    private void ClearList()
    {
        if (_session is not null) return;
        if (_state.Records.Count == 0) { MessageBox.Show("当前列表已为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new CleanupDialog(CleanupKind.List, 0, _state.Records.Count, _batchFolder);
        if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
        InvalidateBrowserSessions();
        _state.Records.Clear();
        _page = 1;
        RefreshAll();
        SaveState();
    }

    private void CleanDeclarationFolder()
    {
        if (_session is not null) return;
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.LastFolder);
            var files = FileCleanupService.GetDeclarationFiles(folder);
            if (files.Count == 0) { MessageBox.Show("当前关单文件夹没有可清理的 PDF 或图片。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var dialog = new CleanupDialog(CleanupKind.Declarations, files.Count, 0, folder);
            if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder);
            _state.Records.RemoveAll(x => !string.IsNullOrWhiteSpace(x.SourcePath) && result.MovedPaths.Contains(Path.GetFullPath(x.SourcePath)));
            BatchScanner.MarkDuplicates(_state.Records);
            _page = 1;
            SaveState();
            RefreshAll();
            ShowCleanupResult("关单清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "关单清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void CleanScreenshotFolder()
    {
        if (_session is not null) return;
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.ScreenshotFolder);
            var files = FileCleanupService.GetScreenshotFiles(folder, _state.Records);
            if (files.Count == 0) { MessageBox.Show("当前截图文件夹没有程序生成的网页截图。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var dialog = new CleanupDialog(CleanupKind.Screenshots, files.Count, 0, folder);
            if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder);
            foreach (var record in _state.Records.Where(x => !string.IsNullOrWhiteSpace(x.ScreenshotPath) && result.MovedPaths.Contains(Path.GetFullPath(x.ScreenshotPath))))
                record.ScreenshotPath = "";
            SaveState();
            RefreshAll();
            ShowCleanupResult("截图清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "截图清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ShowCleanupResult(string title, CleanupResult result)
    {
        var message = $"已将 {result.Moved} 个文件移入 Windows 回收站。";
        if (result.Failed.Count > 0) message += $"\n\n另有 {result.Failed.Count} 个文件未能处理：{string.Join("、", result.Failed.Take(5))}";
        MessageBox.Show(message, title, MessageBoxButtons.OK, result.Failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ---- persistence / chrome ----

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_session is not null)
        {
            _session.Cancel();
            e.Cancel = true;
            ShowToast("正在停止识别，请完成后关闭窗口。");
            return;
        }
        InvalidateBrowserSessions();
        SaveState();
    }

    private void SaveState()
    {
        try
        {
            _state.PageSize = CurrentPageSize();
            _store.Save(_state);
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    private void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    private void BeginWindowDrag()
    {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        if (WindowState == FormWindowState.Maximized) { Region = null; return; }
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 10);
        Region = new Region(path);
        ApplyResponsiveLayout();
        LayoutRecordState();
    }

    protected override void WndProc(ref Message message)
    {
        const int wmNchittest = 0x84, grip = 8;
        if (message.Msg == wmNchittest && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref message);
            var value = (long)message.LParam;
            var point = PointToClient(new Point((short)value, (short)(value >> 16)));
            var left = point.X <= grip;
            var right = point.X >= ClientSize.Width - grip;
            var top = point.Y <= grip;
            var bottom = point.Y >= ClientSize.Height - grip;
            if (left && top) message.Result = (IntPtr)13;
            else if (right && top) message.Result = (IntPtr)14;
            else if (left && bottom) message.Result = (IntPtr)16;
            else if (right && bottom) message.Result = (IntPtr)17;
            else if (left) message.Result = (IntPtr)10;
            else if (right) message.Result = (IntPtr)11;
            else if (top) message.Result = (IntPtr)12;
            else if (bottom) message.Result = (IntPtr)15;
            return;
        }
        base.WndProc(ref message);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
