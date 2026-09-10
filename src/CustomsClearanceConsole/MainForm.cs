using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CustomsClearanceConsole;

internal sealed partial class MainForm : Form
{
    private readonly StateStore _store = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 200 };
    private AppState _state;
    private readonly TextBox _search;
    private int _filterIndex;
    private readonly List<Button> _filterTabs = [];
    private readonly ModernDropDown _pageSize;
    private readonly DataGridView _grid;
    private readonly Panel _emptyState;
    private readonly Label _pageLabel;
    private readonly Label _footer;
    private readonly Label _recordCount;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _scan;
    private Button _export = null!;
    private readonly MetricCard _fileMetric;
    private readonly MetricCard _duplicateMetric;
    private readonly MoneySummaryPanel _moneySummary;
    private ScanSession? _session;
    private Button _directories = null!, _cleanup = null!;
    private Label _directoryPaths = null!, _progressText = null!;
    private ProgressBar _progressBar = null!;
    private TableLayoutPanel _body = null!;
    private Label _emptyLabel = null!;
    private readonly ToolTip _toolTip = new();
    private int _page = 1;
    private List<DeclarationRecord> _visible = [];

    public MainForm()
    {
        _state = _store.Load();
        if (_state.PageSize is not (20 or 50 or 100)) _state.PageSize = 50;
        Text = "关单核验台";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1200, 720);
        Size = new Size(1586, 992);
        BackColor = Theme.Canvas;
        Font = Theme.UiFont(16F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        BuildWorkspace(out _search, out _pageSize, out _scan, out _grid, out _emptyState,
            out _previous, out _pageLabel, out _next, out _footer, out _recordCount,
            out _fileMetric, out _duplicateMetric, out _moneySummary);

        _pageSize.SelectedItem = $"{_state.PageSize} 条";
        if (_pageSize.SelectedIndex < 0) _pageSize.SelectedItem = "50 条";
        _scan.Click += async (_, _) => { if (_session is null) await ScanAsync(); else _session.Cancel(); };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _page = 1; RefreshGrid(); };
        _pageSize.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _previous.Click += (_, _) => { if (_page > 1) { _page--; RefreshGrid(); } };
        _next.Click += (_, _) => { if (_page < PageCount()) { _page++; RefreshGrid(); } };
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellDoubleClick += GridCellDoubleClick;
        FormClosing += (_, e) =>
        {
            if (_session is not null) { _session.Cancel(); e.Cancel = true; _progressText.Text = "正在停止识别，请完成后关闭窗口。"; return; }
            SaveState();
        };
        FormClosed += (_, _) => { _searchTimer.Dispose(); _toolTip.Dispose(); };
        RefreshGrid();
    }

    private void ShowDirectorySettings()
    {
        if (_session is not null) return;
        using var dialog = new DirectorySettingsForm(_state.LastFolder, _state.ScreenshotFolder);
        if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
        _state.LastFolder = dialog.DeclarationFolder; _state.ScreenshotFolder = dialog.ScreenshotFolder; SaveState(); UpdateSummary();
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
        using var session = new ScanSession(plan);
        _session = session;
        _state.Records = session.Completed;
        _page = 1;
        _scan.Text = "取消识别";
        _scan.Image?.Dispose(); _scan.Image = null;
        _progressBar.Maximum = plan.Files.Count;
        _progressBar.Value = 0;
        _progressText.Text = $"准备识别 {plan.Files.Count} 个文件";
        RefreshGrid();
        // Inline progress stays on the UI context; OCR work itself runs on a worker.
        var progress = new InlineProgress<(int Done, int Total, string File)>(x =>
        {
            _progressBar.Value = x.Done;
            _progressText.Text = $"{x.Done} / {x.Total} 已完成 · {x.File}";
        });
        var items = new InlineProgress<DeclarationRecord>(record =>
        {
            session.Completed.Add(record);
            BatchScanner.MarkDuplicates(session.Completed);
            RefreshGrid();
        });
        try { await new BatchScanner().ScanAsync(plan, progress, session.Token, items); }
        catch (OperationCanceledException) { _progressText.Text = $"已取消 · 保留 {session.Completed.Count} 条已完成结果"; }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            BatchScanner.MarkDuplicates(session.Completed);
            _state.Records = BatchScanner.SortRecords(session.Completed).ToList();
            _session = null;
            _scan.Text = "开始识别"; _scan.Image = Theme.ButtonIcon(UiIcon.Start, primary: true);
            SaveState(); RefreshGrid();
        }
    }

    private static string[] DroppedSupportedFiles(DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return [];
        var extensions = new HashSet<string>(BatchScanner.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        return ((string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [])
            .Where(File.Exists).Where(x => extensions.Contains(Path.GetExtension(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void GridDragEnter(object? sender, DragEventArgs e) => e.Effect = DroppedSupportedFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private async void GridDragDrop(object? sender, DragEventArgs e)
    {
        var files = DroppedSupportedFiles(e);
        if (files.Length == 0) return;
        if (files.Length > BatchScanner.MaximumFiles) { MessageBox.Show($"一次最多拖入 {BatchScanner.MaximumFiles} 个关单文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        await ScanDroppedFilesAsync(files);
    }

    private IEnumerable<DeclarationRecord> FilteredRecords()
    {
        var query = _state.Records.AsEnumerable(); var search = _search.Text.Trim();
        if (search.Length > 0) query = query.Where(x => new[] { x.DeclarationNo, x.SourceName, x.Consignee, x.ContractNo }.Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        query = _filterIndex switch { 1 => query.Where(x => !x.IsDuplicate && !x.NeedsAttention), 2 => query.Where(x => x.IsDuplicate), 3 => query.Where(x => x.NeedsAttention), _ => query };
        return BatchScanner.SortRecords(query);
    }

    private int CurrentPageSize()
    {
        var text = _pageSize.SelectedItem?.ToString() ?? "50";
        return int.TryParse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var size) ? size : 50;
    }
    private int PageCount() => Math.Max(1, (int)Math.Ceiling(_visible.Count / (double)CurrentPageSize()));

    private void RefreshGrid()
    {
        _visible = FilteredRecords().ToList(); _page = Math.Clamp(_page, 1, PageCount()); var size = CurrentPageSize(); var items = _visible.Skip((_page - 1) * size).Take(size).ToList(); _grid.Rows.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]; var rowIndex = _grid.Rows.Add((_page - 1) * size + i + 1, item.DeclarationNo.Length > 0 ? item.DeclarationNo : "未识别", EmptyAsDash(item.Consignee), EmptyAsDash(item.ContractNo), EmptyAsDash(item.ExitCustoms), EmptyAsDash(item.DestinationCountry), Formatters.MoneyTotalsLines(item.Totals), item.DisplayStatus, !item.HasValidDeclarationNo ? "不可核验" : _session is not null ? "待识别完成" : item.HasScreenshot ? "已留存\n重新核验" : "核验");
            var row = _grid.Rows[rowIndex]; row.Height = ScalePixels(Math.Max(66, item.Totals.Count * 21 + 16)); row.Tag = item; row.Cells["Verify"].ReadOnly = !item.HasValidDeclarationNo;
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = Convert.ToString(cell.FormattedValue) ?? "";
            row.Cells["Verify"].ToolTipText = item.HasValidDeclarationNo ? "打开核验网站；人工验证后保存长截图" : "未识别出有效报关单号，无法在线核验";
            if (item.IsDuplicate) row.DefaultCellStyle.BackColor = Theme.DangerSoft;
            row.Cells["Status"].Style.ForeColor = item.NeedsAttention ? Theme.Warning : item.IsDuplicate ? Theme.Danger : Theme.Success;
            row.Cells["Verify"].Style.ForeColor = item.HasScreenshot ? Theme.Success : Theme.Blue;
            row.Cells["Status"].ToolTipText = item.AllWarnings;
            row.Cells["No"].ToolTipText = $"{item.DeclarationNo}\n源文件：{item.SourceName}";

        }
        _emptyLabel.Text = _session is not null ? "正在识别，请稍候…" : _state.Records.Count > 0
            ? "没有符合条件的记录\n请调整筛选条件或搜索内容" : "从一批关单开始\n设置读取目录后开始识别，或将 PDF / 图片拖到这里";
        _emptyState.Visible = items.Count == 0; if (_emptyState.Visible) _emptyState.BringToFront(); UpdateSummary();
    }

    private int ScalePixels(int value) => (int)Math.Round(value * DeviceDpi / 96F);

    private static string EmptyPath(string path) => string.IsNullOrWhiteSpace(path) ? "尚未设置" : path;

    private static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void UpdateSummary()
    {
        var duplicateGroups = _state.Records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count();
        _fileMetric.Set(_state.Records.Count.ToString());
        _duplicateMetric.Set(duplicateGroups.ToString());
        _moneySummary.Set(BatchScanner.GrossTotals(_state.Records), BatchScanner.DeduplicatedTotals(_state.Records));
        _body.RowStyles[2].Height = ScalePixels(Math.Max(126, Math.Min(230, 80 + _moneySummary.CurrencyCount * 28)));
        _directoryPaths.Text = $"关单目录   {EmptyPath(_state.LastFolder)}     •     截图目录   {EmptyPath(_state.ScreenshotFolder)}";
        _toolTip.SetToolTip(_directoryPaths, $"关单目录：{EmptyPath(_state.LastFolder)}\n截图目录：{EmptyPath(_state.ScreenshotFolder)}");
        var counts = new[] { _state.Records.Count, _state.Records.Count(x => !x.IsDuplicate && !x.NeedsAttention), _state.Records.Count(x => x.IsDuplicate), _state.Records.Count(x => x.NeedsAttention) };
        var names = new[] { "全部", "正常", "重复", "需关注" };
        for (var i = 0; i < _filterTabs.Count; i++)
        {
            var tab = _filterTabs[i]; tab.Text = $"{names[i]}  {counts[i]}";
            tab.BackColor = i == _filterIndex ? Theme.Blue : Theme.Surface;
            tab.ForeColor = i == _filterIndex ? Color.White : Theme.Muted;
            if (tab is RoundedButton rounded) { rounded.HoverBackColor = i == _filterIndex ? Theme.BlueHover : Theme.HeaderSoft; rounded.BorderColor = i == _filterIndex ? Theme.Blue : Theme.Border; }
            tab.Invalidate();
        }
        _directories.Enabled = _cleanup.Enabled = _session is null;
        _body.RowStyles[4].Height = _session is null ? 0 : ScalePixels(50);
        _pageLabel.Text = $"第 {_page} / {PageCount()} 页"; _previous.Enabled = _page > 1; _next.Enabled = _page < PageCount();
        _footer.Text = $"共 {_visible.Count} 条";
        _recordCount.Text = $"共 {_state.Records.Count} 条记录";
        _export.Enabled = _state.Records.Count > 0 && _session is null;
    }

    private void SelectCellForContextMenu(DataGridViewCellMouseEventArgs e) { if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return; var cell = _grid[e.ColumnIndex, e.RowIndex]; if (cell.Selected) return; _grid.ClearSelection(); cell.Selected = true; _grid.CurrentCell = cell; }
    private void CopySelectedCells() { var selected = _grid.SelectedCells.Cast<DataGridViewCell>().Where(x => x.RowIndex >= 0 && x.ColumnIndex >= 0 && x.Visible).Select(x => (x.RowIndex, x.ColumnIndex, Convert.ToString(x.FormattedValue) ?? "")).ToList(); if (selected.Count == 0) return; try { Clipboard.SetText(FormatCellSelection(selected)); } catch (Exception ex) { AppLog.Write(ex); MessageBox.Show("复制失败，请稍后重试。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    internal static string FormatCellSelection(IEnumerable<(int Row, int Column, string Value)> selected) => string.Join(Environment.NewLine, selected.OrderBy(x => x.Row).ThenBy(x => x.Column).GroupBy(x => x.Row).Select(row => string.Join('\t', row.Select(cell => cell.Value.Replace("\r", " ").Replace("\n", " ")))));

    private async void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record) return;
        if (_session is not null || _grid.Columns[e.ColumnIndex].Name != "Verify" || !record.HasValidDeclarationNo) return;
        if (!Directory.Exists(_state.ScreenshotFolder)) ShowDirectorySettings(); if (!Directory.Exists(_state.ScreenshotFolder)) return;
        using var dialog = new VerificationForm(record, _state.ScreenshotFolder);
        if (ModalPresenter.Show(dialog, this) == DialogResult.OK && dialog.SavedScreenshot is not null)
        {
            foreach (var matching in _state.Records.Where(x => x.DeclarationNo == record.DeclarationNo)) matching.ScreenshotPath = dialog.SavedScreenshot;
            SaveState(); RefreshGrid();
            ShowFileSavedToast(dialog.SavedScreenshot, "核验结果长截图已保存");
        }
        await Task.CompletedTask;
    }

    private void ExportList()
    {
        if (_session is not null || _state.Records.Count == 0) return;
        using var dialog = new SaveFileDialog
        {
            Title = "导出本批全部关单列表",
            Filter = "Markdown 文档 (*.md)|*.md",
            DefaultExt = "md",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"关单列表_{DateTime.Now:yyyyMMdd_HHmmss}.md",
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            MarkdownListExporter.Save(dialog.FileName, BatchScanner.SortRecords(_state.Records).ToList(), DateTime.Now);
            ShowFileSavedToast(dialog.FileName, "关单列表已导出为 Markdown");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show($"导出未完成，请检查保存位置权限或文件占用情况。\n{ex.Message}", "列表导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowFileSavedToast(string filePath, string message)
    {
        var toast = new RoundedPanel
        {
            Size = new Size(392, 70),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = ColorTranslator.FromHtml("#F1FBF5"),
            BorderColor = ColorTranslator.FromHtml("#9ECBB0"),
            Radius = 9,
            AccessibleName = "文件保存成功提示"
        };
        toast.Location = new Point(Math.Max(20, ClientSize.Width - toast.Width - 30), Math.Max(20, ClientSize.Height - toast.Height - 30));
        var text = new Label
        {
            Text = message,
            Location = new Point(18, 0),
            Size = new Size(225, toast.Height),
            Font = Theme.UiFont(14F),
            ForeColor = Theme.Success,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var open = Theme.SecondaryButton("打开位置");
        open.Location = new Point(268, 15);
        open.Size = new Size(106, 40);
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

    private void GridCellDoubleClick(object? sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record || !File.Exists(record.SourcePath)) return; try { Process.Start(new ProcessStartInfo(record.SourcePath) { UseShellExecute = true }); } catch (Exception ex) { AppLog.Write(ex); } }

    private void ClearList()
    {
        if (_session is not null) return;
        if (_state.Records.Count == 0) { MessageBox.Show("当前列表已为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!ConfirmationDialog.Confirm(this, "确认清理当前列表？", "将移除当前已经读取的全部关单记录。", "此操作不会删除关单目录中的源文件。")) return;
        _state.Records.Clear(); _page = 1; RefreshGrid(); SaveState();
    }

    private void CleanDeclarationFolder()
    {
        if (_session is not null) return;
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.LastFolder); var files = FileCleanupService.GetDeclarationFiles(folder);
            if (files.Count == 0) { MessageBox.Show("当前关单文件夹没有可清理的 PDF 或图片。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!ConfirmFileCleanup(folder, files.Count, "关单源文件（PDF / 图片，仅当前目录）")) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder); _state.Records.RemoveAll(x => !string.IsNullOrWhiteSpace(x.SourcePath) && result.MovedPaths.Contains(Path.GetFullPath(x.SourcePath))); BatchScanner.MarkDuplicates(_state.Records); _page = 1; SaveState(); RefreshGrid(); ShowCleanupResult("关单清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "关单清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void CleanScreenshotFolder()
    {
        if (_session is not null) return;
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.ScreenshotFolder); var files = FileCleanupService.GetScreenshotFiles(folder, _state.Records);
            if (files.Count == 0) { MessageBox.Show("当前截图文件夹没有程序生成的网页截图。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!ConfirmFileCleanup(folder, files.Count, "程序生成的核验截图（仅当前目录）")) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder); foreach (var record in _state.Records.Where(x => !string.IsNullOrWhiteSpace(x.ScreenshotPath) && result.MovedPaths.Contains(Path.GetFullPath(x.ScreenshotPath)))) record.ScreenshotPath = ""; SaveState(); RefreshGrid(); ShowCleanupResult("截图清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "截图清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private bool ConfirmFileCleanup(string folder, int count, string scope) =>
        ConfirmationDialog.Confirm(this, "核对清理范围 · 1 / 2", $"{scope}\n共 {count} 个文件，将移入 Windows 回收站。", folder) &&
        ConfirmationDialog.Confirm(this, "最后确认 · 2 / 2", $"确认将这 {count} 个文件移入 Windows 回收站？\n{scope}", folder);

    private void ShowCleanupResult(string title, CleanupResult result) { var message = $"已将 {result.Moved} 个文件移入 Windows 回收站。"; if (result.Failed.Count > 0) message += $"\n\n另有 {result.Failed.Count} 个文件未能处理：{string.Join("、", result.Failed.Take(5))}"; MessageBox.Show(message, title, MessageBoxButtons.OK, result.Failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning); }
    private void SaveState() { try { _state.PageSize = CurrentPageSize(); _store.Save(_state); } catch (Exception ex) { AppLog.Write(ex); } }
    private void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    private void BeginWindowDrag() { if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal; ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        if (WindowState == FormWindowState.Maximized) { Region = null; return; }
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 10);
        Region = new Region(path);
    }

    protected override void WndProc(ref Message message)
    {
        const int wmNchittest = 0x84, grip = 8;
        if (message.Msg == wmNchittest && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref message); var value = (long)message.LParam; var point = PointToClient(new Point((short)value, (short)(value >> 16))); var left = point.X <= grip; var right = point.X >= ClientSize.Width - grip; var top = point.Y <= grip; var bottom = point.Y >= ClientSize.Height - grip;
            if (left && top) message.Result = (IntPtr)13; else if (right && top) message.Result = (IntPtr)14; else if (left && bottom) message.Result = (IntPtr)16; else if (right && bottom) message.Result = (IntPtr)17; else if (left) message.Result = (IntPtr)10; else if (right) message.Result = (IntPtr)11; else if (top) message.Result = (IntPtr)12; else if (bottom) message.Result = (IntPtr)15;
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
