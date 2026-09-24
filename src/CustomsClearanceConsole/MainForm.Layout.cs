namespace CustomsClearanceConsole;

internal sealed partial class MainForm
{
    private void BuildWorkspace()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, BackColor = Theme.Canvas };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTokens.Metrics.Header));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);

        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, BackColor = Theme.Canvas };
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));   // title row
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));  // stats row
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // progress strip
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // lock bar
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // records panel

        _body.Controls.Add(BuildTitleRow(), 0, 0);
        _body.Controls.Add(BuildStatsRow(), 0, 1);

        _progressStrip = new ProgressStrip { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        _body.Controls.Add(_progressStrip, 0, 2);
        _lockBar = new LockBar { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        _body.Controls.Add(_lockBar, 0, 3);

        _body.Controls.Add(BuildRecordsPanel(), 0, 4);
        root.Controls.Add(_body, 0, 1);
        Controls.Add(root);

        _toast = new ToastControl { Anchor = AnchorStyles.Bottom, Location = new Point(0, ClientSize.Height - 50) };
        Controls.Add(_toast);
        _toast.BringToFront();
        ClientSizeChanged += (_, _) =>
        {
            ApplyResponsiveLayout();
            _toast.Location = new Point((ClientSize.Width - _toast.Width) / 2, ClientSize.Height - _toast.Height - 18);
        };
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.HeaderBg, AccessibleName = "应用标题栏" };
        var logoBack = new RoundedPanel { Location = new Point(20, 10), Size = new Size(28, 28), Radius = 6, BackColor = Theme.Accent, BorderColor = Theme.Accent };
        var logo = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        var appIcon = LoadApplicationIcon();
        if (appIcon is not null) logo.Image = appIcon;
        logoBack.Controls.Add(logo);
        header.Controls.Add(logoBack);
        var title = new Label
        {
            Text = "关单核验台",
            Location = new Point(60, 0),
            Size = new Size(160, UiTokens.Metrics.Header),
            Font = AppFonts.Ui(15F, UiWeight.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        header.Controls.Add(title);
        var divider = new Panel { Location = new Point(186, 14), Size = new Size(1, 20), BackColor = ColorTranslator.FromHtml("#5C6D82") };
        header.Controls.Add(divider);
        var version = new Label
        {
            Text = "v" + (Application.ProductVersion.Split('+')[0]),
            Location = new Point(197, 0),
            Size = new Size(90, UiTokens.Metrics.Header),
            Font = AppFonts.Mono(12F),
            ForeColor = ColorTranslator.FromHtml("#C9D6E8"),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        header.Controls.Add(version);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, BackColor = Color.Transparent, Padding = new Padding(0, 9, 10, 9) };
        _settings = Theme.IconButton("设置", new RoundedButton
        {
            BackColor = ColorTranslator.FromHtml("#213855"),
            BorderColor = ColorTranslator.FromHtml("#5C6D82"),
            ForeColor = Color.White,
            HoverBackColor = ColorTranslator.FromHtml("#354A65"),
            PressedBackColor = ColorTranslator.FromHtml("#40566F"),
            DisabledFill = Theme.HeaderBg,
            Radius = 6,
            Size = new Size(78, 30),
            Font = AppFonts.Ui(13F, UiWeight.Medium)
        }, Ui2.SettingsWhite, 16, DeviceDpi);
        _settings.Click += (_, _) => ShowDirectorySettings();
        actions.Controls.Add(_settings);
        foreach (var glyph in new[] { CaptionGlyph.Minimize, CaptionGlyph.Maximize, CaptionGlyph.Close })
        {
            var button = new WindowCaptionButton(glyph) { Margin = new Padding(0), Size = new Size(46, 30) };
            var captured = glyph;
            button.Click += (_, _) =>
            {
                if (captured == CaptionGlyph.Close) Close();
                else if (captured == CaptionGlyph.Minimize) WindowState = FormWindowState.Minimized;
                else { ToggleMaximize(); button.RefreshWindowState(); }
            };
            actions.Controls.Add(button);
        }
        header.Controls.Add(actions);
        foreach (var control in new Control[] { header, title, version, divider, logoBack })
            control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginWindowDrag(); };
        foreach (var control in new Control[] { header, title, version, logoBack })
            control.DoubleClick += (_, _) => ToggleMaximize();
        return header;
    }

    private static Bitmap? LoadApplicationIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "ui-v2", "icons", "app", "app-icon-48.png");
            if (File.Exists(path)) { using var source = new Bitmap(path); return new Bitmap(source); }
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico)) { using var icon = new Icon(ico); return icon.ToBitmap(); }
        }
        catch (Exception ex) { AppLog.Write(ex); }
        return null;
    }

    private Control BuildTitleRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Canvas };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _titleBlock = new TitleBlock { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
        row.Controls.Add(_titleBlock, 0, 0);
        _exportButton = Theme.IconButton("导出列表", Theme.SecondaryButton(""), Ui2.Export, 16, DeviceDpi);
        _exportButton.Dock = DockStyle.Fill;
        _exportButton.Margin = new Padding(0, 11, 10, 11);
        _exportButton.AccessibleName = "导出列表";
        _exportButton.Click += (_, _) => ShowExportMenu();
        _exportButton.Disposed += (_, _) => _exportMenu?.Dispose();
        row.Controls.Add(_exportButton, 1, 0);
        _scan = Theme.IconButton("开始识别", Theme.PrimaryButton(""), Ui2.Play, 16, DeviceDpi);
        _scan.Dock = DockStyle.Fill;
        _scan.Margin = new Padding(0, 11, 0, 11);
        _scan.AccessibleName = "开始识别";
        _scan.Click += async (_, _) => { if (_session is null) await ScanAsync(); else _session.Cancel(); };
        row.Controls.Add(_scan, 2, 0);
        return row;
    }

    private Control BuildStatsRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Canvas };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _statsRow = row;
        _kpi = new KpiPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0) };
        _moneySummary = new MoneySummaryPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        row.Controls.Add(_kpi, 0, 0);
        row.Controls.Add(_moneySummary, 1, 0);
        return row;
    }

    private Control BuildRecordsPanel()
    {
        var panel = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, BorderColor = Theme.Border, Radius = 8, Margin = Padding.Empty };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(1), Margin = Padding.Empty, BackColor = Theme.Surface };
        _recordsContent = content;
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(16, 0, 16, 0), BackColor = Theme.Surface };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 372));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _toolbar = toolbar;
        _filterSegmented = new FilterSegmented { Dock = DockStyle.Fill, Margin = new Padding(0, 11, 0, 11) };
        _filterSegmented.SegmentSelected += (_, index) => { _filterIndex = index; _page = 1; RefreshGrid(); };
        _filterSegmented.Disposed += (_, _) => { };
        toolbar.Controls.Add(_filterSegmented, 0, 0);
        _search = new TextBox { PlaceholderText = "搜索单号、文件名、收货人、合同号", BorderStyle = BorderStyle.None, Font = Theme.UiFont(13F), AccessibleName = "搜索关单记录", BackColor = Color.White };
        _searchHost = new SearchField(_search) { Dock = DockStyle.Fill, Margin = new Padding(0, 11, 0, 11) };
        toolbar.Controls.Add(_searchHost, 1, 0);
        _cleanupButton = Theme.IconButton("清理", Theme.QuietButton(""), Ui2.TrashInk, 16, DeviceDpi);
        _cleanupButton.Dock = DockStyle.Fill;
        _cleanupButton.Margin = new Padding(0, 11, 0, 11);
        _cleanupButton.AccessibleName = "清理";
        _cleanupButton.Click += (_, _) => { if (_session is null) _cleanupMenu.Show(_cleanupButton, _cleanupButton.Height + 6, true); };
        _cleanupButton.Disposed += (_, _) => _cleanupMenu.Dispose();
        toolbar.Controls.Add(_cleanupButton, 3, 0);
        content.Controls.Add(toolbar, 0, 0);

        _grid = BuildGrid();
        _recordState = new RecordStatePanel { Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        _recordState.ActionInvoked += (_, action) => { if (action == "选择关单目录") ShowDirectorySettings(); };
        _gridHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, AllowDrop = true };
        _gridHost.Controls.Add(_grid);
        _gridHost.Controls.Add(_recordState);
        foreach (var target in new Control[] { _gridHost, _grid, _recordState })
        {
            target.AllowDrop = true;
            target.DragEnter += GridDragEnter;
            target.DragOver += GridDragOver;
            target.DragDrop += GridDragDrop;
            target.DragLeave += (_, _) => HideDropFeedback();
        }
        content.Controls.Add(_gridHost, 0, 1);
        content.Controls.Add(BuildFooter(), 0, 2);
        panel.Controls.Add(content);
        return panel;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(16, 0, 16, 0), BackColor = Theme.Surface };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        _footer = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.UiFont(12.5F), AutoEllipsis = true };
        _previous = Theme.QuietButton("上一页");
        _previous.Dock = DockStyle.Fill;
        _previous.Margin = new Padding(0, 8, 6, 8);
        _previous.Click += (_, _) => { if (_page > 1) { _page--; RefreshGrid(); } };
        _pageLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text, Font = Theme.MonoFont(12.5F, true) };
        _next = Theme.QuietButton("下一页");
        _next.Dock = DockStyle.Fill;
        _next.Margin = new Padding(6, 8, 0, 8);
        _next.Click += (_, _) => { if (_page < PageCount()) { _page++; RefreshGrid(); } };
        var perPage = new Label { Text = "每页", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted, Font = Theme.UiFont(12.5F) };
        _pageSize = new ModernDropDown { Dock = DockStyle.Fill, Margin = new Padding(8, 8, 0, 8), ItemHeight = 30, OpenUpward = true, UseMonoValue = true, AccessibleName = "每页显示条数" };
        _pageSize.Items.AddRange(["50 条", "100 条", "200 条"]);
        _pageSize.SelectedIndexChanged += (_, _) => { _page = 1; RefreshGrid(); };
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(_previous, 1, 0);
        footer.Controls.Add(_pageLabel, 2, 0);
        footer.Controls.Add(_next, 3, 0);
        footer.Controls.Add(perPage, 4, 0);
        footer.Controls.Add(_pageSize, 5, 0);
        return footer;
    }

    private DataGridView BuildGrid()
    {
        var grid = new RecordGrid
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Surface,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = true,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 38,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowTemplate = { Height = 54 },
            ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
            ScrollBars = ScrollBars.Both,
            AllowUserToOrderColumns = false
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.PanelSubtle,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(12.5F, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            SelectionBackColor = Theme.PanelSubtle,
            SelectionForeColor = Theme.Muted,
            Padding = new Padding(8, 0, 8, 0)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(13.5F),
            SelectionBackColor = UiTokens.Status.CellSelected,
            SelectionForeColor = Theme.Text,
            Padding = new Padding(8, 0, 8, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            NullValue = "—",
            WrapMode = DataGridViewTriState.False
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        grid.GridColor = Theme.Divider;
        AddColumn(grid, "Index", "#", DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "Status", "状态", DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "No", "报关单号 / 来源文件", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "Consignee", "境外收货人", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "Contract", "合同协议号", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "Port", "出境关别", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "Dest", "目的国", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "PortDest", "出境关别 / 目的国", DataGridViewContentAlignment.MiddleLeft);
        AddColumn(grid, "Amount", "关单总货值", DataGridViewContentAlignment.MiddleRight);
        AddColumn(grid, "Detail", "查看", DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "Verify", "网页核验", DataGridViewContentAlignment.MiddleCenter);

        grid.CellPainting += PaintRecordCell;
        grid.CellClick += GridCellClick;
        grid.CellDoubleClick += GridCellDoubleClick;
        grid.CellMouseDown += (_, e) => { SelectCellForContextMenu(e); if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0) _copyMenu.ShowAt(Cursor.Position, grid.SelectedCells.Count > 0); };
        grid.KeyDown += GridKeyDown;
        grid.ColumnWidthChanged += (_, e) =>
        {
            if (_applyingWidths || e.Column is null || !e.Column.Visible) return;
            if (e.Column.Name == "Consignee")
                _manualWidths[e.Column.Name] = e.Column.Width;
            else
                _manualWidths[e.Column.Name] = e.Column.Width;
        };
        grid.ColumnHeaderMouseDoubleClick += (_, _) => { _manualWidths.Clear(); ApplyGridColumns(); };
        // No CellFormatting blanking: custom painting happens in CellPainting with
        // e.Handled = true, so real cell values stay available for copy, tooltips and
        // accessibility. Blanks would make No/Amount/Status copy as empty (R3-2).
        _copyMenu = new CopyContextMenu(CopySelectedCells);
        grid.Disposed += (_, _) => _copyMenu.Dispose();
        return grid;
    }

    private static void AddColumn(DataGridView grid, string name, string header, DataGridViewContentAlignment alignment) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Resizable = DataGridViewTriState.True,
            MinimumWidth = name switch { "Index" => 28, "Status" => 64, "No" => 150, _ => 48 },
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment, NullValue = "—" }
        });

    private void ApplyResponsiveLayout()
    {
        if (IsDisposed || _body is null || _grid is null || _recordsContent is null) return;
        SuspendLayout();
        _body.SuspendLayout();
        var dpi = DeviceDpi;
        var logicalWidth = (int)Math.Round(ClientSize.Width * 96.0 / dpi);
        var logicalHeight = (int)Math.Round(ClientSize.Height * 96.0 / dpi);
        _layout = Responsive.Compute(logicalWidth, logicalHeight);
        int S(int px) => (int)Math.Round(px * dpi / 96.0);
        _body.Padding = new Padding(S(24), S(_layout.ContentPaddingY), S(24), S(_layout.ContentPaddingY));
        _body.RowStyles[0].Height = S(_layout.CompactHeight ? 56 : 62);
        _body.RowStyles[1].Height = S(Math.Max(126, Math.Min(280,
            96 + _moneySummary.RowCount * (_layout.AmountRow + 4) + (_moneySummary.UnconfirmedRowCount > 0 ? 26 + _moneySummary.UnconfirmedRowCount * 26 : 0))));
        _body.RowStyles[2].Height = _session is null ? 0 : S(_layout.CompactHeight ? 92 : 104);
        _body.RowStyles[3].Height = _session is null ? 0 : S(_layout.CompactHeight ? 34 : 38);
        _statsRow.ColumnStyles[0].Width = S(_layout.KpiPanelWidth);
        _toolbar.ColumnStyles[0].Width = S(372);
        _toolbar.ColumnStyles[1].Width = S(_layout.SearchWidth + 8);
        _recordsContent.RowStyles[0].Height = S(_layout.Toolbar);
        _grid.ColumnHeadersHeight = S(38);
        _grid.RowTemplate.Height = S(_layout.TableRow);
        ApplyGridColumns();
        ResumeLayout(true);
        _body.ResumeLayout(true);
    }

    private void ApplyGridColumns()
    {
        if (_grid.Columns.Count == 0) return;
        var dpi = DeviceDpi;
        int S(int px) => (int)Math.Round(px * dpi / 96.0);
        var table = _layout.Table;
        _applyingWidths = true;
        try
        {
            var merged = table.PortDestMerged;
            _grid.Columns["Port"].Visible = !merged;
            _grid.Columns["Dest"].Visible = !merged;
            _grid.Columns["PortDest"].Visible = merged;
            var inner = ClientSize.Width == 0 ? S(1100) : ClientSize.Width - S(48) - S(2) - S(32);
            void Set(string name, int logical, bool fill = false)
            {
                var column = _grid.Columns[name];
                if (!column.Visible) return;
                if (_manualWidths.TryGetValue(name, out var manual) && !fill) { column.Width = Math.Max(column.MinimumWidth, manual); return; }
                column.Width = Math.Max(column.MinimumWidth, S(logical));
            }
            Set("Index", table.Index);
            Set("Status", table.Status);
            Set("No", table.Number);
            Set("Contract", table.Contract);
            Set("Port", table.Port);
            Set("Dest", table.Dest);
            Set("PortDest", table.PortDest);
            Set("Amount", table.Amount);
            Set("Detail", table.Detail);
            Set("Verify", table.Verify);
            var used = new[] { "Index", "Status", "No", "Contract", "Port", "Dest", "PortDest", "Amount", "Detail", "Verify" }
                .Where(name => _grid.Columns[name].Visible)
                .Sum(name => _grid.Columns[name].Width);
            var consignee = _grid.Columns["Consignee"];
            if (_manualWidths.TryGetValue("Consignee", out var manualConsignee))
                consignee.Width = Math.Max(consignee.MinimumWidth, manualConsignee);
            else
                consignee.Width = Math.Max(consignee.MinimumWidth, inner - used);
        }
        finally { _applyingWidths = false; }
    }

    /// <summary>Double-buffered grid so repaints during resize do not flicker.</summary>
    private sealed class RecordGrid : DataGridView
    {
        public RecordGrid()
        {
            DoubleBuffered = true;
        }
    }
}
