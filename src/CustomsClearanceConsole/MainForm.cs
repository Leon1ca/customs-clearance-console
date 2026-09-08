using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CustomsClearanceConsole;

internal sealed class MainForm : Form
{
    private readonly StateStore _store = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 200 };
    private AppState _state;
    private readonly TextBox _search;
    private readonly ModernDropDown _filter;
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
    private readonly MetricCard _grossMetric;
    private readonly MetricCard _deduplicatedMetric;
    private CancellationTokenSource? _scanCancellation;
    private int _page = 1;
    private List<DeclarationRecord> _visible = [];

    public MainForm()
    {
        _state = _store.Load();
        if (_state.UiSchemaVersion < 4) { _state.PageSize = 50; _state.UiSchemaVersion = 4; }
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

        var header = BuildHeader();
        var commandBar = BuildCommandBar();
        var metrics = BuildMetrics(out _fileMetric, out _duplicateMetric, out _grossMetric, out _deduplicatedMetric);
        var records = BuildRecordsPanel(out _search, out _filter, out _pageSize, out _scan, out _grid, out _emptyState, out _previous, out _pageLabel, out _next, out _footer, out _recordCount);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(43, 17, 43, 38),
            ColumnCount = 1, RowCount = 3
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(metrics, 0, 0);
        body.Controls.Add(records, 0, 2);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0), BackColor = Theme.Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 79));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(commandBar, 0, 1); layout.Controls.Add(body, 0, 2);
        Controls.Add(layout);

        _pageSize.SelectedItem = $"{_state.PageSize} 条";
        if (_pageSize.SelectedIndex < 0) _pageSize.SelectedItem = "50 条";
        _scan.Click += async (_, _) => { if (_scanCancellation is null) await ScanAsync(); else _scanCancellation.Cancel(); };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _page = 1; RefreshGrid(); };
        _filter.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _pageSize.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _previous.Click += (_, _) => { if (_page > 1) { _page--; RefreshGrid(); } };
        _next.Click += (_, _) => { if (_page < PageCount()) { _page++; RefreshGrid(); } };
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellDoubleClick += GridCellDoubleClick;
        FormClosing += (_, _) => SaveState();
        RefreshGrid();
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Navy, Margin = new Padding(0), AccessibleName = "应用标题栏" };
        var logoPath = Path.Combine(AppContext.BaseDirectory, "assets", "app-icon-40.png");
        var logo = new PictureBox
        {
            Image = File.Exists(logoPath) ? Image.FromFile(logoPath) : Icon?.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(28, 21), Size = new Size(40, 40),
            AccessibleName = "关单核验台图标"
        };
        var title = new Label
        {
            Text = "关单核验台", ForeColor = Color.White, Font = Theme.UiFont(25F, FontStyle.Bold),
            Location = new Point(92, 20), Size = new Size(260, 42), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
        };
        var minimize = new WindowCaptionButton(CaptionGlyph.Minimize) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var maximize = new WindowCaptionButton(CaptionGlyph.Maximize) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var close = new WindowCaptionButton(CaptionGlyph.Close) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        void PositionCaptionButtons()
        {
            minimize.Location = new Point(header.ClientSize.Width - 212, 13);
            maximize.Location = new Point(header.ClientSize.Width - 141, 13);
            close.Location = new Point(header.ClientSize.Width - 70, 13);
            maximize.RefreshWindowState();
        }
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => { ToggleMaximize(); maximize.RefreshWindowState(); };
        close.Click += (_, _) => Close();
        header.Controls.AddRange([logo, title, minimize, maximize, close]);
        header.Resize += (_, _) => PositionCaptionButtons();
        PositionCaptionButtons();
        foreach (var control in new Control[] { header, logo, title })
        {
            control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginWindowDrag(); };
            control.DoubleClick += (_, _) => { ToggleMaximize(); maximize.RefreshWindowState(); };
        }
        return header;
    }

    private Control BuildCommandBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CommandBar, Margin = new Padding(0) };
        panel.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, panel.Height - 1, panel.Width, panel.Height - 1);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.CommandBar, Padding = new Padding(43, 17, 43, 17), ColumnCount = 7, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 171));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 49));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 153));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 25));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 139));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        var heading = new Label { Text = "关单概览", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.FieldText, Font = Theme.UiFont(20F, FontStyle.Bold) };
        var directories = Theme.IconButton("目录设置", UiIcon.Directory, tonal: true); directories.Dock = DockStyle.Fill; directories.Click += (_, _) => ShowDirectorySettings();
        var declarationClean = Theme.IconButton("关单清理", UiIcon.DeclarationClean, danger: true); declarationClean.Dock = DockStyle.Fill; declarationClean.Click += (_, _) => CleanDeclarationFolder();
        var screenshotClean = Theme.IconButton("截图清理", UiIcon.ScreenshotClean, danger: true); screenshotClean.Dock = DockStyle.Fill; screenshotClean.Click += (_, _) => CleanScreenshotFolder();
        var divider1 = new Panel { Width = 1, Height = 30, BackColor = Theme.Border, Anchor = AnchorStyles.None };
        var divider2 = new Panel { Width = 1, Height = 30, BackColor = Theme.Border, Anchor = AnchorStyles.None };
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(directories, 1, 0); layout.Controls.Add(divider1, 2, 0); layout.Controls.Add(declarationClean, 3, 0); layout.Controls.Add(divider2, 4, 0); layout.Controls.Add(screenshotClean, 5, 0);
        panel.Controls.Add(layout); return panel;
    }

    private Control BuildMetrics(out MetricCard file, out MetricCard duplicate, out MetricCard gross, out MetricCard deduplicated)
    {
        var frame = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0), Radius = 8, BorderColor = Theme.Border, AccentColor = Theme.Blue, AccentHeight = 2 };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 4, RowCount = 1, Padding = new Padding(1) };
        for (var i = 0; i < 4; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        file = new MetricCard("本批关单", "0", "份", "", MetricIcon.File, 40F) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        duplicate = new MetricCard("重复单号", "0", "组", "", MetricIcon.Duplicate, 40F) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        gross = new MetricCard("去重前总价", "—", "", "", MetricIcon.Gross, 15F, money: true) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        deduplicated = new MetricCard("去重后总价", "—", "", "", MetricIcon.Deduplicated, 15F, money: true) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        layout.Controls.Add(file, 0, 0); layout.Controls.Add(duplicate, 1, 0); layout.Controls.Add(gross, 2, 0); layout.Controls.Add(deduplicated, 3, 0);
        layout.CellPaint += (_, e) => { if (e.Column > 0 && e.Row == 0) e.Graphics.DrawLine(new Pen(Theme.Border), e.CellBounds.Left, e.CellBounds.Top + 18, e.CellBounds.Left, e.CellBounds.Bottom - 18); };
        frame.Controls.Add(layout); return frame;
    }

    private Control BuildRecordsPanel(out TextBox search, out ModernDropDown filter, out ModernDropDown pageSize, out Button scan, out DataGridView grid, out Panel empty, out Button previous, out Label pageLabel, out Button next, out Label footer, out Label recordCount)
    {
        var frame = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0), Radius = 8, BorderColor = Theme.Border };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 1, RowCount = 4, Padding = new Padding(1) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, Padding = new Padding(24, 14, 24, 14) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 169)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        heading.Controls.Add(new Label { Text = "关单记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.UiFont(24F, FontStyle.Bold), AutoSize = true }, 0, 0);
        recordCount = new Label { Text = "共 0 条记录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.UiFont(19F), Padding = new Padding(18, 0, 0, 0) };
        heading.Controls.Add(recordCount, 1, 0);
        scan = Theme.IconButton("开始识别", UiIcon.Start, primary: true); scan.Dock = DockStyle.Fill; scan.Margin = new Padding(0, 0, 12, 0);
        _export = Theme.SecondaryButton("列表导出");
        _export.Dock = DockStyle.Fill;
        _export.Margin = new Padding(0, 0, 12, 0);
        _export.AccessibleName = "列表导出";
        _export.Click += (_, _) => ExportList();
        var clear = Theme.IconButton("列表清理", UiIcon.ListClean, danger: true); clear.Dock = DockStyle.Fill; clear.Click += (_, _) => ClearList();
        heading.Controls.Add(scan, 3, 0); heading.Controls.Add(_export, 4, 0); heading.Controls.Add(clear, 5, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, Padding = new Padding(24, 6, 24, 10), BackColor = ColorTranslator.FromHtml("#F7F9FC") };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 412)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 293)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        toolbar.Controls.Add(FieldLabel("单号搜索"), 0, 0); toolbar.Controls.Add(FieldLabel("记录筛选"), 2, 0); toolbar.Controls.Add(FieldLabel("每页显示"), 4, 0);
        search = new TextBox { PlaceholderText = "输入报关单编号搜索", BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Theme.FieldText, Font = Theme.UiFont(18F), AccessibleName = "搜索报关单编号" };
        filter = new ModernDropDown { Dock = DockStyle.Fill, Margin = new Padding(0) }; filter.Items.AddRange(["全部记录", "正常记录", "重复单号", "核验异常"]); filter.SelectedIndex = 0;
        pageSize = new ModernDropDown { Dock = DockStyle.Fill, Margin = new Padding(0) }; pageSize.Items.AddRange(["20 条", "50 条", "100 条"]);
        toolbar.Controls.Add(new SearchField(search) { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 1);
        toolbar.Controls.Add(filter, 2, 1);
        toolbar.Controls.Add(pageSize, 4, 1);

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AllowDrop = true };
        grid = BuildGrid(); empty = BuildEmptyState(); gridHost.Controls.Add(grid); gridHost.Controls.Add(empty); empty.BringToFront();
        grid.AllowDrop = true; empty.AllowDrop = true;
        foreach (var target in new Control[] { gridHost, grid, empty }) { target.DragEnter += GridDragEnter; target.DragDrop += GridDragDrop; }
        var pagination = BuildPagination(out previous, out pageLabel, out next, out footer);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(gridHost, 0, 2); layout.Controls.Add(pagination, 0, 3);
        frame.Controls.Add(layout); return frame;
    }

    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.FieldText, Font = Theme.UiFont(17F, FontStyle.Bold) };

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true, RowHeadersVisible = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = true, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 58, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowTemplate = { Height = 56 }, ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.HeaderSoft, ForeColor = Theme.Text, Font = Theme.UiFont(18F, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter, SelectionBackColor = Theme.HeaderSoft, SelectionForeColor = Theme.Text };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.UiFont(17F), SelectionBackColor = ColorTranslator.FromHtml("#DCE8F8"), SelectionForeColor = Theme.Text, Padding = new Padding(8, 0, 8, 0), Alignment = DataGridViewContentAlignment.MiddleLeft, NullValue = "—" };
        grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#F8FAFD"); grid.GridColor = Theme.Border;
        AddTextColumn(grid, "Index", "序号", 7, 48, DataGridViewContentAlignment.MiddleCenter); AddTextColumn(grid, "No", "报关单编号", 14, 128); AddTextColumn(grid, "Consignee", "境外收货人", 15, 128); AddTextColumn(grid, "Contract", "合同协议号", 13, 108); AddTextColumn(grid, "Customs", "出境关别", 11, 84); AddTextColumn(grid, "Country", "目的国", 10, 70); AddTextColumn(grid, "Total", "关单总货值", 13, 112, DataGridViewContentAlignment.MiddleRight); AddTextColumn(grid, "Status", "状态", 8, 72, DataGridViewContentAlignment.MiddleCenter);
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Verify", HeaderText = "校验", Text = "校验", UseColumnTextForButtonValue = true, FillWeight = 8, MinimumWidth = 70, Resizable = DataGridViewTriState.True, FlatStyle = FlatStyle.Flat });
        var menu = new CopyContextMenu(CopySelectedCells);
        grid.Disposed += (_, _) => menu.Dispose();
        grid.CellMouseDown += (_, e) =>
        {
            SelectCellForContextMenu(e);
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                menu.ShowAt(Cursor.Position, grid.SelectedCells.Count > 0);
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedCells(); e.Handled = true; e.SuppressKeyPress = true;
            }
            else if ((e.Shift && e.KeyCode == Keys.F10) || e.KeyCode == Keys.Apps)
            {
                var cell = grid.CurrentCell;
                var point = cell is null ? new Point(20, 20) : new Point(
                    grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true).Left + 16,
                    grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true).Bottom - 4);
                menu.ShowAt(grid.PointToScreen(point), grid.SelectedCells.Count > 0);
                e.Handled = true; e.SuppressKeyPress = true;
            }
        };
        return grid;
    }

    private static void AddTextColumn(DataGridView grid, string name, string header, float weight, int minimumWidth, DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, MinimumWidth = minimumWidth, Resizable = DataGridViewTriState.True, DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment, NullValue = "—" } });

    private Panel BuildEmptyState()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
        var icon = new PictureBox { Image = UiIcons.Create(UiIcon.Start, Theme.Muted, 40), SizeMode = PictureBoxSizeMode.CenterImage, Size = new Size(48, 48), AccessibleName = "空列表图标" };
        var label = new Label { Text = "暂无关单记录", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, Font = Theme.UiFont(18F), Size = new Size(380, 40), AccessibleName = "暂无关单记录，可拖入文件识别" };
        void CenterContent()
        {
            var left = Math.Max(0, (panel.ClientSize.Width - label.Width) / 2);
            var top = Math.Max(8, (panel.ClientSize.Height - 92) / 2);
            icon.Location = new Point((panel.ClientSize.Width - icon.Width) / 2, top);
            label.Location = new Point(left, top + 52);
        }
        panel.Controls.AddRange([icon, label]); panel.Resize += (_, _) => CenterContent(); CenterContent();
        return panel;
    }

    private Control BuildPagination(out Button previous, out Label pageLabel, out Button next, out Label footer)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 9, 24, 9), ColumnCount = 4, RowCount = 1, BackColor = Theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        footer = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.UiFont(18F), AutoEllipsis = true };
        previous = Theme.SecondaryButton("上一页"); previous.Dock = DockStyle.Fill; previous.Margin = new Padding(0, 0, 8, 0);
        pageLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text, Font = Theme.UiFont(18F) };
        next = Theme.SecondaryButton("下一页"); next.Dock = DockStyle.Fill; next.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(footer, 0, 0); layout.Controls.Add(previous, 1, 0); layout.Controls.Add(pageLabel, 2, 0); layout.Controls.Add(next, 3, 0); return layout;
    }

    private void ShowDirectorySettings()
    {
        using var dialog = new DirectorySettingsForm(_state.LastFolder, _state.ScreenshotFolder);
        if (ModalPresenter.Show(dialog, this) != DialogResult.OK) return;
        _state.LastFolder = dialog.DeclarationFolder; _state.ScreenshotFolder = dialog.ScreenshotFolder; SaveState(); UpdateSummary();
    }

    private async Task ScanAsync()
    {
        if (!Directory.Exists(_state.LastFolder)) { MessageBox.Show("请先在“目录设置”中选择有效的关单文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        await RunScanAsync((scanner, progress, token, items) => scanner.ScanAsync(_state.LastFolder, progress, token, items));
    }

    private async Task ScanDroppedFilesAsync(IReadOnlyList<string> files)
    {
        if (_scanCancellation is not null) { MessageBox.Show("当前正在识别，请先取消后再拖入新批次。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        await RunScanAsync((scanner, progress, token, items) => scanner.ScanFilesAsync(files, progress, token, items));
    }

    private async Task RunScanAsync(Func<BatchScanner, IProgress<(int Done, int Total, string File)>, CancellationToken, IProgress<DeclarationRecord>, Task<List<DeclarationRecord>>> scanAction)
    {
        _scanCancellation = new CancellationTokenSource(); _state.Records = []; _page = 1; RefreshGrid(); _scan.Text = "取消识别"; _scan.Image = null;
        var progress = new Progress<(int Done, int Total, string File)>(x => { _scan.Text = x.Done >= x.Total ? "整理结果…" : $"识别中 {x.Done}/{x.Total}"; });
        var items = new InlineProgress<DeclarationRecord>(record => { _state.Records.Add(record); BatchScanner.MarkDuplicates(_state.Records); RefreshGrid(); });
        try { _state.Records = await scanAction(new BatchScanner(), progress, _scanCancellation.Token, items); SaveState(); RefreshGrid(); }
        catch (OperationCanceledException) { BatchScanner.MarkDuplicates(_state.Records); SaveState(); RefreshGrid(); MessageBox.Show($"识别已取消，已保留完成的 {_state.Records.Count} 条结果。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { AppLog.Write(ex); SaveState(); RefreshGrid(); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _scanCancellation.Dispose(); _scanCancellation = null; _scan.Text = "开始识别"; _scan.Image = Theme.ButtonIcon(UiIcon.Start, primary: true); UpdateSummary(); }
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
        if (search.Length > 0) query = query.Where(x => x.DeclarationNo.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        query = _filter.SelectedIndex switch { 1 => query.Where(x => !x.IsDuplicate && x.Status is not ("需关注" or "识别失败")), 2 => query.Where(x => x.IsDuplicate), 3 => query.Where(x => x.Status is "需关注" or "识别失败"), _ => query };
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
            var item = items[i]; var rowIndex = _grid.Rows.Add((_page - 1) * size + i + 1, item.DeclarationNo.Length > 0 ? item.DeclarationNo : "未识别", EmptyAsDash(item.Consignee), EmptyAsDash(item.ContractNo), EmptyAsDash(item.ExitCustoms), EmptyAsDash(item.DestinationCountry), item.DisplayTotal, item.ScreenshotPath.Length > 0 ? "已核验" : item.Status, "校验");
            var row = _grid.Rows[rowIndex]; row.Height = 56; row.Tag = item; row.Cells["Verify"].ReadOnly = item.DeclarationNo.Length != 18;
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = Convert.ToString(cell.FormattedValue) ?? "";
            row.Cells["Verify"].ToolTipText = item.DeclarationNo.Length == 18 ? "打开核验网站；人工验证后保存长截图" : "未识别出有效报关单号，无法在线核验";
            if (item.IsDuplicate) { row.DefaultCellStyle.BackColor = Theme.DangerSoft; row.DefaultCellStyle.ForeColor = Theme.Danger; row.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#FFDAD6"); row.DefaultCellStyle.SelectionForeColor = Theme.Danger; row.Cells["No"].Style.Font = Theme.UiFont(17F, FontStyle.Bold); }
            if (item.Status is "需关注" or "识别失败") row.Cells["Status"].Style.ForeColor = Theme.Warning;
            row.Cells["Status"].ToolTipText = item.Warning; row.Cells["No"].ToolTipText = $"{item.DeclarationNo}\n源文件：{item.SourceName}";
        }
        _emptyState.Visible = items.Count == 0; if (_emptyState.Visible) _emptyState.BringToFront(); UpdateSummary();
    }

    private static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void UpdateSummary()
    {
        var duplicateGroups = _state.Records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count();
        _fileMetric.Set(_state.Records.Count.ToString(), "");
        _duplicateMetric.Set(duplicateGroups.ToString(), "");
        _grossMetric.Set(Formatters.MoneyTotalsLines(BatchScanner.GrossTotals(_state.Records)), "");
        _deduplicatedMetric.Set(Formatters.MoneyTotalsLines(BatchScanner.DeduplicatedTotals(_state.Records)), "");
        _pageLabel.Text = $"第 {_page} / {PageCount()} 页"; _previous.Enabled = _page > 1; _next.Enabled = _page < PageCount();
        _footer.Text = $"共 {_visible.Count} 条";
        _recordCount.Text = $"共 {_state.Records.Count} 条记录";
        _export.Enabled = _state.Records.Count > 0 && _scanCancellation is null;
    }

    private void SelectCellForContextMenu(DataGridViewCellMouseEventArgs e) { if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return; var cell = _grid[e.ColumnIndex, e.RowIndex]; if (cell.Selected) return; _grid.ClearSelection(); cell.Selected = true; _grid.CurrentCell = cell; }
    private void CopySelectedCells() { var selected = _grid.SelectedCells.Cast<DataGridViewCell>().Where(x => x.RowIndex >= 0 && x.ColumnIndex >= 0 && x.Visible).Select(x => (x.RowIndex, x.ColumnIndex, Convert.ToString(x.FormattedValue) ?? "")).ToList(); if (selected.Count == 0) return; try { Clipboard.SetText(FormatCellSelection(selected)); } catch (Exception ex) { AppLog.Write(ex); MessageBox.Show("复制失败，请稍后重试。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    internal static string FormatCellSelection(IEnumerable<(int Row, int Column, string Value)> selected) => string.Join(Environment.NewLine, selected.OrderBy(x => x.Row).ThenBy(x => x.Column).GroupBy(x => x.Row).Select(row => string.Join('\t', row.Select(cell => cell.Value.Replace("\r", " ").Replace("\n", " ")))));

    private async void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record) return;
        if (_grid.Columns[e.ColumnIndex].Name != "Verify" || record.DeclarationNo.Length != 18) return;
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
        if (_scanCancellation is not null || _state.Records.Count == 0) return;
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
        if (_state.Records.Count == 0) { MessageBox.Show("当前列表已为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!ConfirmationDialog.Confirm(this, "确认清理当前列表？", "将移除当前已经读取的全部关单记录。", "此操作不会删除关单目录中的源文件。")) return;
        _state.Records.Clear(); _page = 1; RefreshGrid(); SaveState();
    }

    private void CleanDeclarationFolder()
    {
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.LastFolder); var files = FileCleanupService.GetDeclarationFiles(folder);
            if (files.Count == 0) { MessageBox.Show("当前关单文件夹没有可清理的 PDF 或图片。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!ConfirmationDialog.Confirm(this, "确认清理关单目录？", "将把关单读取目录中的 PDF/图片移入 Windows 回收站。", "请确认当前目录中没有需要保留的关单文件。")) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder); _state.Records.RemoveAll(x => !string.IsNullOrWhiteSpace(x.SourcePath) && result.MovedPaths.Contains(Path.GetFullPath(x.SourcePath))); _page = 1; SaveState(); RefreshGrid(); ShowCleanupResult("关单清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "关单清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void CleanScreenshotFolder()
    {
        try
        {
            var folder = FileCleanupService.ValidateTargetFolder(_state.ScreenshotFolder); var files = FileCleanupService.GetScreenshotFiles(folder, _state.Records);
            if (files.Count == 0) { MessageBox.Show("当前截图文件夹没有程序生成的网页截图。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!ConfirmationDialog.Confirm(this, "确认清理截图目录？", "将把截图保存目录中的网页截图移入 Windows 回收站。", "请确认当前目录中没有需要保留的核验截图。")) return;
            var result = FileCleanupService.MoveToRecycleBin(files, folder); foreach (var record in _state.Records.Where(x => !string.IsNullOrWhiteSpace(x.ScreenshotPath) && result.MovedPaths.Contains(Path.GetFullPath(x.ScreenshotPath)))) record.ScreenshotPath = ""; SaveState(); RefreshGrid(); ShowCleanupResult("截图清理", result);
        }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, "截图清理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

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
            if (message.Result != IntPtr.Zero) return;
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
