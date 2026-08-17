using System.Diagnostics;

namespace CustomsClearanceConsole;

internal sealed class MainForm : Form
{
    private readonly StateStore _store = new();
    private AppState _state;
    private readonly TextBox _folderText;
    private readonly TextBox _search;
    private readonly ComboBox _filter;
    private readonly ComboBox _pageSize;
    private readonly ComboBox _browser;
    private readonly DataGridView _grid;
    private readonly Label _pageLabel;
    private readonly Label _footer;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _scan;
    private readonly MetricCard _fileMetric;
    private readonly MetricCard _duplicateMetric;
    private readonly MetricCard _totalMetric;
    private int _page = 1;
    private List<DeclarationRecord> _visible = [];

    public MainForm()
    {
        _state = _store.Load();
        Text = "关单核验台";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        Size = new Size(1380, 860);
        BackColor = Theme.Canvas;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        var header = BuildHeader();
        var folderPanel = BuildFolderPanel(out _folderText, out _scan);
        var metrics = BuildMetrics(out _fileMetric, out _duplicateMetric, out _totalMetric);
        var toolbar = BuildToolbar(out _search, out _filter, out _pageSize, out _browser);
        _grid = BuildGrid();
        var pagination = BuildPagination(out _previous, out _pageLabel, out _next, out _footer);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 14), BackColor = Theme.Canvas,
            ColumnCount = 1, RowCount = 5
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.Controls.Add(folderPanel, 0, 0); content.Controls.Add(metrics, 0, 1); content.Controls.Add(toolbar, 0, 2);
        content.Controls.Add(_grid, 0, 3); content.Controls.Add(pagination, 0, 4);
        Controls.Add(content); Controls.Add(header);

        _folderText.Text = _state.LastFolder;
        _browser.SelectedItem = _state.BrowserPreference;
        if (_browser.SelectedIndex < 0) _browser.SelectedIndex = 0;
        _pageSize.SelectedItem = _state.PageSize.ToString();
        if (_pageSize.SelectedIndex < 0) _pageSize.SelectedItem = "20";

        _scan.Click += async (_, _) => await ScanAsync();
        _search.TextChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _filter.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _pageSize.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        _previous.Click += (_, _) => { if (_page > 1) { _page--; RefreshGrid(); } };
        _next.Click += (_, _) => { if (_page < PageCount()) { _page++; RefreshGrid(); } };
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellDoubleClick += GridCellDoubleClick;
        FormClosing += (_, _) => SaveState();
        RefreshGrid();
    }

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Theme.Navy };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Navy, Padding = new Padding(22, 10, 22, 10),
            ColumnCount = 6, RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        var logo = new PictureBox
        {
            Image = Icon?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 8, 1), AccessibleName = "关单核验台图标"
        };
        var title = new Label
        {
            Text = "关单核验台", ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
        };
        var rules = Theme.SecondaryButton("识别与边界规则"); rules.Dock = DockStyle.Fill; rules.Margin = new Padding(6, 0, 6, 0);
        var clearList = Theme.SecondaryButton("清空列表"); clearList.Dock = DockStyle.Fill; clearList.Margin = new Padding(6, 0, 6, 0);
        var clearFolder = Theme.DangerButton("清空文件夹"); clearFolder.Dock = DockStyle.Fill; clearFolder.Margin = new Padding(6, 0, 0, 0);
        rules.Click += (_, _) => ShowRules();
        clearList.Click += (_, _) => ClearList();
        clearFolder.Click += (_, _) => ClearFolder();
        layout.Controls.Add(logo, 0, 0); layout.Controls.Add(title, 1, 0);
        layout.Controls.Add(rules, 3, 0); layout.Controls.Add(clearList, 4, 0); layout.Controls.Add(clearFolder, 5, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Panel BuildFolderPanel(out TextBox folderText, out Button scanButton)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10),
            ColumnCount = 3, RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        folderText = new TextBox
        {
            Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(Font.FontFamily, 10F), Margin = new Padding(0, 5, 10, 5)
        };
        var choose = Theme.SecondaryButton("选择关单文件夹"); choose.Dock = DockStyle.Fill; choose.Margin = new Padding(0, 0, 10, 0);
        scanButton = Theme.PrimaryButton("开始识别"); scanButton.Dock = DockStyle.Fill; scanButton.Margin = new Padding(0);
        choose.Click += (_, _) => ChooseFolder();
        panel.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, panel.ClientRectangle, Theme.Border, ButtonBorderStyle.Solid);
        layout.Controls.Add(folderText, 0, 0); layout.Controls.Add(choose, 1, 0); layout.Controls.Add(scanButton, 2, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildMetrics(out MetricCard file, out MetricCard duplicate, out MetricCard total)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Canvas, Margin = new Padding(0, 0, 0, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        file = new MetricCard("本批文件", "0", "最多 200 个，仅当前文件夹", Theme.Blue) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        duplicate = new MetricCard("重复单号", "0 组", "重复记录置顶标红", Theme.Danger) { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 8, 0) };
        total = new MetricCard("去重合计价格", "—", "按币种分别合计", Theme.Success, 11.5F) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        layout.Controls.Add(file, 0, 0); layout.Controls.Add(duplicate, 1, 0); layout.Controls.Add(total, 2, 0);
        return layout;
    }

    private Control BuildToolbar(out TextBox search, out ComboBox filter, out ComboBox pageSize, out ComboBox browser)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 9, 10, 9),
            ColumnCount = 8, RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        search = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, PlaceholderText = "搜索单号、收货人、合同号…", BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 10, 3) };
        filter = new ComboBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 1, 10, 1) };
        filter.Items.AddRange(["全部记录", "仅重复", "仅需关注", "仅已核验"]); filter.SelectedIndex = 0;
        var pageLabel = new Label { Text = "每页", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Margin = new Padding(0) };
        pageSize = new ComboBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 1, 8, 1) };
        pageSize.Items.AddRange(["20", "50", "100", "200"]);
        var browserLabel = new Label { Text = "核验浏览器", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 6, 0) };
        browser = new ComboBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 1, 10, 1) };
        browser.Items.AddRange(["Edge", "Chrome"]);
        var screenshots = Theme.SecondaryButton("截图目录"); screenshots.Dock = DockStyle.Fill; screenshots.Margin = new Padding(0); screenshots.Click += (_, _) => ChooseScreenshotFolder();
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(filter, 1, 0); layout.Controls.Add(pageLabel, 2, 0); layout.Controls.Add(pageSize, 3, 0);
        layout.Controls.Add(browserLabel, 5, 0); layout.Controls.Add(browser, 6, 0); layout.Controls.Add(screenshots, 7, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ReadOnly = true, RowHeadersVisible = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = true, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 43,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowTemplate = { Height = 43 }, ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Navy, ForeColor = Color.White, Font = new Font(Font.FontFamily, 9F, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter, SelectionBackColor = Theme.Navy };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Theme.Text, SelectionBackColor = ColorTranslator.FromHtml("#E8F0FE"), SelectionForeColor = Theme.Text, Padding = new Padding(4), Alignment = DataGridViewContentAlignment.MiddleLeft };
        grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#FAFCFF");
        grid.GridColor = Theme.Border;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Index", HeaderText = "序号", Width = 58, MinimumWidth = 45, Resizable = DataGridViewTriState.True, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", HeaderText = "报关单编号", Width = 190, MinimumWidth = 120, Resizable = DataGridViewTriState.True });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Consignee", HeaderText = "境外收货人", Width = 300, MinimumWidth = 140, Resizable = DataGridViewTriState.True });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Contract", HeaderText = "合同协议号", Width = 150, MinimumWidth = 90, Resizable = DataGridViewTriState.True });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Customs", HeaderText = "出境关别", Width = 120, MinimumWidth = 80, Resizable = DataGridViewTriState.True });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Country", HeaderText = "目的国", Width = 90, MinimumWidth = 70, Resizable = DataGridViewTriState.True });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "关单总货值", Width = 160, MinimumWidth = 110, Resizable = DataGridViewTriState.True, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", Width = 120, MinimumWidth = 90, Resizable = DataGridViewTriState.True, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter } });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Verify", HeaderText = "操作", Text = "校验", UseColumnTextForButtonValue = true, Width = 80, MinimumWidth = 68, Resizable = DataGridViewTriState.True, FlatStyle = FlatStyle.Flat });
        var menu = new ContextMenuStrip();
        var copy = new ToolStripMenuItem("复制选中内容") { ShortcutKeyDisplayString = "Ctrl+C" };
        copy.Click += (_, _) => CopySelectedCells();
        menu.Items.Add(copy);
        menu.Opening += (_, _) => copy.Enabled = grid.SelectedCells.Count > 0;
        grid.ContextMenuStrip = menu;
        grid.CellMouseDown += (_, e) => SelectCellForContextMenu(e);
        grid.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.C) return;
            CopySelectedCells(); e.Handled = true; e.SuppressKeyPress = true;
        };
        return grid;
    }

    private Control BuildPagination(out Button previous, out Label pageLabel, out Button next, out Label footer)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8), ColumnCount = 4, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        footer = new Label { Text = "右键复制选中单元格；拖动列标题分隔线可调整列宽", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, AutoEllipsis = true };
        previous = Theme.SecondaryButton("上一页"); previous.Dock = DockStyle.Fill; previous.Margin = new Padding(4, 0, 4, 0);
        pageLabel = new Label { Text = "第 1 / 1 页", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        next = Theme.SecondaryButton("下一页"); next.Dock = DockStyle.Fill; next.Margin = new Padding(4, 0, 0, 0);
        layout.Controls.Add(footer, 0, 0); layout.Controls.Add(previous, 1, 0); layout.Controls.Add(pageLabel, 2, 0); layout.Controls.Add(next, 3, 0);
        panel.Controls.Add(layout); return panel;
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含关单的文件夹（仅识别当前层，不扫描子文件夹）", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_state.LastFolder) ? _state.LastFolder : "" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _folderText.Text = dialog.SelectedPath; _state.LastFolder = dialog.SelectedPath; }
    }

    private void ChooseScreenshotFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择核验长截图保存文件夹", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_state.ScreenshotFolder) ? _state.ScreenshotFolder : "" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _state.ScreenshotFolder = dialog.SelectedPath; SaveState(); }
    }

    private async Task ScanAsync()
    {
        if (!Directory.Exists(_folderText.Text)) { MessageBox.Show("请先选择有效的关单文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        _scan.Enabled = false;
        using var progressDialog = new ProgressDialog();
        var progress = new Progress<(int Done, int Total, string File)>(x => progressDialog.UpdateProgress(x.Done, x.Total, x.File));
        progressDialog.Show(this);
        try
        {
            _state.Records = await new BatchScanner().ScanAsync(_folderText.Text, progress, progressDialog.Cancellation.Token);
            _state.LastFolder = _folderText.Text; _page = 1; SaveState(); RefreshGrid();
        }
        catch (OperationCanceledException) { MessageBox.Show("识别已取消，原有历史未被覆盖。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { AppLog.Write(ex); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { progressDialog.Close(); _scan.Enabled = true; }
    }

    private IEnumerable<DeclarationRecord> FilteredRecords()
    {
        var query = _state.Records.AsEnumerable();
        var search = _search.Text.Trim();
        if (search.Length > 0) query = query.Where(x => new[] { x.DeclarationNo, x.Consignee, x.ContractNo, x.ExitCustoms, x.DestinationCountry, x.SourceName }.Any(v => v.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        query = _filter.SelectedIndex switch
        {
            1 => query.Where(x => x.IsDuplicate),
            2 => query.Where(x => x.Status is "需关注" or "识别失败"),
            3 => query.Where(x => x.ScreenshotPath.Length > 0),
            _ => query
        };
        return BatchScanner.SortRecords(query);
    }

    private int CurrentPageSize() => int.TryParse(_pageSize.SelectedItem?.ToString(), out var size) ? size : 20;
    private int PageCount() => Math.Max(1, (int)Math.Ceiling(_visible.Count / (double)CurrentPageSize()));

    private void RefreshGrid()
    {
        _visible = FilteredRecords().ToList(); _page = Math.Min(_page, PageCount());
        var size = CurrentPageSize(); var items = _visible.Skip((_page - 1) * size).Take(size).ToList();
        _grid.Rows.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var rowIndex = _grid.Rows.Add((_page - 1) * size + i + 1, item.DeclarationNo.Length > 0 ? item.DeclarationNo : "未识别", item.Consignee, item.ContractNo, item.ExitCustoms, item.DestinationCountry, item.DisplayTotal, item.ScreenshotPath.Length > 0 ? "已核验" : item.Status, "校验");
            var row = _grid.Rows[rowIndex]; row.Tag = item; row.Cells["Verify"].ReadOnly = item.DeclarationNo.Length != 18;
            row.Cells["Verify"].ToolTipText = item.DeclarationNo.Length == 18 ? "打开在线核验并保存长截图" : "未识别出有效报关单号，无法在线核验";
            if (item.IsDuplicate)
            {
                row.DefaultCellStyle.BackColor = Theme.DangerSoft; row.DefaultCellStyle.ForeColor = Theme.Danger;
                row.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#FFE2DF"); row.DefaultCellStyle.SelectionForeColor = Theme.Danger;
                row.Cells["No"].Style.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            }
            if (item.Status is "需关注" or "识别失败") row.Cells["Status"].Style.ForeColor = Theme.Warning;
            row.Cells["Status"].ToolTipText = item.Warning;
            row.Cells["No"].ToolTipText = $"源文件：{item.SourceName}";
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var duplicateGroups = _state.Records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count();
        var deduplicated = Formatters.MoneyTotals(BatchScanner.DeduplicatedTotals(_state.Records));
        var grossCompact = Formatters.MoneyTotalsCompact(BatchScanner.GrossTotals(_state.Records));
        var deduplicatedCompact = Formatters.MoneyTotalsCompact(BatchScanner.DeduplicatedTotals(_state.Records));
        _fileMetric.Set(_state.Records.Count.ToString(), $"唯一关单 {_state.Records.Count(x => x.IsCanonical)} 个");
        _duplicateMetric.Set($"{duplicateGroups} 组", duplicateGroups == 0 ? "未发现重复" : $"涉及 {_state.Records.Count(x => x.IsDuplicate)} 个文件");
        _totalMetric.Set(deduplicated, $"显示{_visible.Count}条 · 去重前{grossCompact}\n去重后{deduplicatedCompact} · 按币种合计");
        _pageLabel.Text = $"第 {_page} / {PageCount()} 页"; _previous.Enabled = _page > 1; _next.Enabled = _page < PageCount();
        _footer.Text = "右键复制选中单元格；拖动列标题分隔线可向左或向右调整列宽";
    }

    private void SelectCellForContextMenu(DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var cell = _grid[e.ColumnIndex, e.RowIndex];
        if (cell.Selected) return;
        _grid.ClearSelection();
        cell.Selected = true;
        _grid.CurrentCell = cell;
    }

    private void CopySelectedCells()
    {
        var selected = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(x => x.RowIndex >= 0 && x.ColumnIndex >= 0 && x.Visible)
            .Select(x => (x.RowIndex, x.ColumnIndex, Convert.ToString(x.FormattedValue) ?? ""))
            .ToList();
        if (selected.Count == 0) return;
        var text = FormatCellSelection(selected);
        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show("复制失败，请稍后重试。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static string FormatCellSelection(IEnumerable<(int Row, int Column, string Value)> selected) =>
        string.Join(Environment.NewLine, selected
            .OrderBy(x => x.Row).ThenBy(x => x.Column)
            .GroupBy(x => x.Row)
            .Select(row => string.Join('\t', row.Select(cell => cell.Value.Replace("\r", " ").Replace("\n", " ")))));

    private async void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Verify") return;
        if (_grid.Rows[e.RowIndex].Tag is not DeclarationRecord record || record.DeclarationNo.Length != 18) return;
        if (!Directory.Exists(_state.ScreenshotFolder)) ChooseScreenshotFolder();
        if (!Directory.Exists(_state.ScreenshotFolder)) return;
        _state.BrowserPreference = _browser.SelectedItem?.ToString() ?? "Edge";
        using var dialog = new VerificationForm(record, _state.BrowserPreference, _state.ScreenshotFolder);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SavedScreenshot is not null)
        {
            foreach (var matching in _state.Records.Where(x => x.DeclarationNo == record.DeclarationNo)) matching.ScreenshotPath = dialog.SavedScreenshot;
            SaveState(); RefreshGrid();
            if (MessageBox.Show("长截图已保存。是否打开所在文件夹？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.SavedScreenshot}\"") { UseShellExecute = true });
        }
        await Task.CompletedTask;
    }

    private void GridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record || !File.Exists(record.SourcePath)) return;
        try { Process.Start(new ProcessStartInfo(record.SourcePath) { UseShellExecute = true }); } catch (Exception ex) { AppLog.Write(ex); }
    }

    private void ClearList()
    {
        _state.Records.Clear();
        _page = 1; RefreshGrid();
        SaveState();
    }

    private void ClearFolder()
    {
        if (!Directory.Exists(_folderText.Text))
        {
            MessageBox.Show("请先选择有效的关单文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var folder = Path.GetFullPath(_folderText.Text);
        var root = Path.GetPathRoot(folder) ?? "";
        if (folder.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("为保护数据，不能对磁盘根目录执行清空文件夹。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var extensions = new HashSet<string>(BatchScanner.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(x => extensions.Contains(Path.GetExtension(x))).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show("当前文件夹没有可清理的 PDF 或图片关单。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var first = MessageBox.Show(
            $"将把当前文件夹中的 {files.Count} 个 PDF/图片文件移入 Windows 回收站。\n\n文件夹：{folder}\n\n不会处理子文件夹，也不会删除其他格式文件。是否继续？",
            "清空文件夹（第一次确认）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (first != DialogResult.Yes) return;
        var second = MessageBox.Show(
            "这是第二次确认。执行后，关单文件将从当前文件夹移走，并同步从识别列表中移除。\n\n确定继续吗？",
            "清空文件夹（第二次确认）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (second != DialogResult.Yes) return;

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                deleted.Add(Path.GetFullPath(file));
            }
            catch (Exception ex)
            {
                AppLog.Write($"移入回收站失败：{file}\n{ex}");
                failures.Add(Path.GetFileName(file));
            }
        }

        _state.Records.RemoveAll(x => !string.IsNullOrWhiteSpace(x.SourcePath) && deleted.Contains(Path.GetFullPath(x.SourcePath)));
        _page = 1; RefreshGrid(); SaveState();
        var message = $"已将 {deleted.Count} 个文件移入 Windows 回收站。";
        if (failures.Count > 0) message += $"\n\n另有 {failures.Count} 个文件未能处理：{string.Join("、", failures.Take(5))}";
        MessageBox.Show(message, Text, MessageBoxButtons.OK, failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ShowRules()
    {
        const string text = "识别规则\n\n" +
            "• 仅扫描所选文件夹当前层；每批最多 200 个 PDF/图片文件。\n" +
            "• PDF 优先读取文字层；扫描件/图片使用 Tesseract + PP-OCRv5 双引擎，并对 6 个字段逐项复核。\n" +
            "• 双引擎结果不直接混合；一致时通过，一方缺失时补全，双方合理但冲突时标记‘需关注’。\n" +
            "• 相同报关单号视为同一关单，全部置顶标红；去重合计只采用完整度更高的一条。\n" +
            "• 价格逐项读取‘总价/币制’，不同币种分别合计，不做汇率换算。\n" +
            "• 出境关别按申报海关/出境关别组合展示，例如‘义乌/北仑’。\n\n" +
            "边界处理\n\n" +
            "• 文件超过 200 个时整批停止，不截断，避免误以为已全部处理。\n" +
            "• 损坏、加密、格式异常或字段缺失的文件保留在列表并标记‘需关注/识别失败’，其缺失金额不进入合计。\n" +
            "• 图片宽度接近或低于 1000 像素时，放大无法恢复已丢失笔画；建议使用原 PDF/原图或宽度不低于 1500 像素的截图。\n" +
            "• 重复单内容不一致时，去重合计采用字段更完整、置信度更高的一条，并给出提示。\n" +
            "• ‘清空列表’只清理当前识别记录，不删除源关单与截图。\n" +
            "• ‘清空文件夹’经过两次确认后，仅把当前层的 PDF/图片移入 Windows 回收站；不递归处理子文件夹，也不删除其他格式文件。\n" +
            "• 同名单号重复截图不会覆盖旧文件。\n" +
            "• 核验网站改版导致自动填写失败时，可在已打开的页面手动输入，长截图功能仍可继续尝试。";
        MessageBox.Show(text, "识别与边界规则", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveState()
    {
        try
        {
            _state.LastFolder = _folderText.Text; _state.BrowserPreference = _browser.SelectedItem?.ToString() ?? "Edge"; _state.PageSize = CurrentPageSize();
            _store.Save(_state);
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }
}
