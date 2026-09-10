namespace CustomsClearanceConsole;

internal sealed partial class MainForm
{
    private void BuildWorkspace(out TextBox search, out ModernDropDown pageSize, out Button scan,
        out DataGridView grid, out Panel empty, out Button previous, out Label pageLabel,
        out Button next, out Label footer, out Label recordCount, out MetricCard file,
        out MetricCard duplicate, out MoneySummaryPanel money)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(32, 16, 32, 22), Margin = Padding.Empty };
        foreach (var height in new[] { 72, 52, 126, 20, 0 }) _body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0, 4, 0, 14), Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        var headline = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        headline.Controls.Add(new Label { Text = "关单工作台", Location = new Point(0, 0), Size = new Size(360, 30), Font = Theme.UiFont(25, FontStyle.Bold), ForeColor = Theme.Text });
        headline.Controls.Add(new Label { Text = "批量识别、重复检查与网页核验", Location = new Point(1, 34), Size = new Size(460, 20), Font = Theme.UiFont(13), ForeColor = Theme.Muted });
        _export = Theme.IconButton("导出列表", UiIcon.Export); _export.Dock = DockStyle.Fill; _export.Margin = new Padding(0, 6, 12, 4); _export.Click += (_, _) => ExportList();
        scan = Theme.IconButton("开始识别", UiIcon.Start, primary: true); scan.Dock = DockStyle.Fill; scan.Margin = new Padding(0, 6, 0, 4);
        heading.Controls.Add(headline, 0, 0); heading.Controls.Add(_export, 1, 0); heading.Controls.Add(scan, 2, 0);
        _body.Controls.Add(heading, 0, 0);
        var directories = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10), Padding = Padding.Empty };
        directories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); directories.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _directoryPaths = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UiFont(12), ForeColor = Theme.Muted, AutoEllipsis = true, Margin = Padding.Empty };
        _directories = Theme.IconButton("目录设置", UiIcon.Directory); _directories.Dock = DockStyle.Fill; _directories.Margin = Padding.Empty; _directories.Click += (_, _) => ShowDirectorySettings();
        directories.Controls.Add(_directoryPaths, 0, 0); directories.Controls.Add(_directories, 1, 0); _body.Controls.Add(directories, 0, 1);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        file = new MetricCard("本批关单", "份", MetricIcon.File) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
        duplicate = new MetricCard("重复单号", "组", MetricIcon.Duplicate) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
        money = new MoneySummaryPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        metrics.Controls.Add(file, 0, 0); metrics.Controls.Add(duplicate, 1, 0); metrics.Controls.Add(money, 2, 0); _body.Controls.Add(metrics, 0, 2);
        var progress = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 0, 0, 10) };
        progress.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); progress.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
        _progressText = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont(13), ForeColor = Theme.Blue, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        _progressBar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Margin = Padding.Empty, AccessibleName = "批次识别进度" };
        progress.Controls.Add(_progressText, 0, 0); progress.Controls.Add(_progressBar, 0, 1); _body.Controls.Add(progress, 0, 4);
        var records = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(1), Margin = Padding.Empty };
        foreach (var height in new[] { 60, 58 }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        var recordHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(20, 10, 20, 8), Margin = Padding.Empty };
        recordHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); recordHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); recordHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        recordHeading.Controls.Add(new Label { Text = "关单记录", Dock = DockStyle.Fill, Font = Theme.UiFont(18, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        recordCount = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont(12), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
        _cleanup = Theme.IconButton("清理 ▾", UiIcon.Clean); _cleanup.Dock = DockStyle.Fill; _cleanup.Margin = Padding.Empty;
        var menu = new ContextMenuStrip { Font = Theme.UiFont(14), ShowImageMargin = false, Padding = new Padding(6) };
        menu.Items.Add("清理当前列表", null, (_, _) => ClearList()); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清理关单源文件…", null, (_, _) => CleanDeclarationFolder()); menu.Items.Add("清理核验截图…", null, (_, _) => CleanScreenshotFolder());
        _cleanup.Click += (_, _) => { if (_session is null) menu.Show(_cleanup, new Point(0, _cleanup.Height)); };
        _cleanup.Disposed += (_, _) => menu.Dispose();
        recordHeading.Controls.Add(recordCount, 1, 0); recordHeading.Controls.Add(_cleanup, 2, 0); content.Controls.Add(recordHeading, 0, 0);
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(20, 4, 20, 12), Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 462)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++)
        {
            var index = i; var button = Theme.SecondaryButton(""); button.Size = new Size(105, 38); button.Margin = new Padding(0, 0, 8, 0);
            button.AccessibleName = new[] { "全部记录", "正常记录", "重复记录", "需关注记录" }[i];
            button.Click += (_, _) => { _filterIndex = index; _page = 1; RefreshGrid(); }; _filterTabs.Add(button); tabs.Controls.Add(button);
        }
        search = new TextBox { PlaceholderText = "搜索单号、文件、收货人或合同", BorderStyle = BorderStyle.None, Font = Theme.UiFont(14), AccessibleName = "搜索关单记录" };
        toolbar.Controls.Add(tabs, 0, 0); toolbar.Controls.Add(new SearchField(search) { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(46, 10, 10, 4) }, 2, 0); content.Controls.Add(toolbar, 0, 1);
        grid = BuildGrid(); empty = BuildEmptyState(); var gridHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, AllowDrop = true }; gridHost.Controls.Add(grid); gridHost.Controls.Add(empty);
        foreach (var target in new Control[] { gridHost, grid, empty }) { target.AllowDrop = true; target.DragEnter += GridDragEnter; target.DragDrop += GridDragDrop; }
        content.Controls.Add(gridHost, 0, 2);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pageSize = new ModernDropDown { Dock = DockStyle.Fill, Margin = new Padding(20, 11, 0, 11), AccessibleName = "每页显示条数" }; pageSize.Items.AddRange(["20 条", "50 条", "100 条"]);
        bottom.Controls.Add(pageSize, 0, 0); bottom.Controls.Add(BuildPagination(out previous, out pageLabel, out next, out footer), 1, 0); content.Controls.Add(bottom, 0, 3);
        records.Controls.Add(content); _body.Controls.Add(records, 0, 5); root.Controls.Add(_body, 0, 1); Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Navy, AccessibleName = "应用标题栏" };
        var title = new Label { Text = "关单核验台", Location = new Point(66, 0), Size = new Size(300, 56), Font = Theme.UiFont(17, FontStyle.Bold), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
        var logo = new PictureBox { Image = Icon?.ToBitmap(), Location = new Point(28, 13), Size = new Size(30, 30), SizeMode = PictureBoxSizeMode.Zoom };
        header.Controls.AddRange([logo, title]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 168, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        foreach (var glyph in new[] { CaptionGlyph.Minimize, CaptionGlyph.Maximize, CaptionGlyph.Close })
        {
            var button = new WindowCaptionButton(glyph) { Margin = Padding.Empty };
            button.Click += (_, _) => { if (glyph == CaptionGlyph.Close) Close(); else if (glyph == CaptionGlyph.Minimize) WindowState = FormWindowState.Minimized; else { ToggleMaximize(); button.RefreshWindowState(); } };
            actions.Controls.Add(button);
        }
        header.Controls.Add(actions);
        foreach (var control in new Control[] { header, title, logo }) { control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginWindowDrag(); }; control.DoubleClick += (_, _) => ToggleMaximize(); }
        return header;
    }

    private void PaintRecordCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeclarationRecord record) return;
        if (_grid.Columns[e.ColumnIndex].Name != "No") return;
        e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var text = selected ? Theme.Text : record.IsDuplicate ? Theme.Danger : Theme.Text;
        var bounds = e.CellBounds;
        using var numberFont = Theme.MonoFont(14);
        using var sourceFont = Theme.UiFont(11);
        TextRenderer.DrawText(e.Graphics!, record.DeclarationNo.Length == 0 ? "未识别" : record.DeclarationNo, numberFont, new Rectangle(bounds.X + ScalePixels(10), bounds.Y + ScalePixels(12), bounds.Width - ScalePixels(20), ScalePixels(22)), text, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(e.Graphics!, record.SourceName, sourceFont, new Rectangle(bounds.X + ScalePixels(10), bounds.Y + ScalePixels(36), bounds.Width - ScalePixels(20), ScalePixels(18)), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (record.IsDuplicate) { using var stripe = new SolidBrush(Theme.Danger); e.Graphics!.FillRectangle(stripe, bounds.X, bounds.Y + ScalePixels(10), ScalePixels(3), bounds.Height - ScalePixels(20)); }
        e.Handled = true;
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true, RowHeadersVisible = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = true, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 46, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowTemplate = { Height = 66 }, ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.HeaderSoft, ForeColor = Theme.Text, Font = Theme.UiFont(13F, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter, SelectionBackColor = Theme.HeaderSoft, SelectionForeColor = Theme.Text };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.UiFont(14F), SelectionBackColor = ColorTranslator.FromHtml("#E6EEF4"), SelectionForeColor = Theme.Text, Padding = new Padding(8, 0, 8, 0), Alignment = DataGridViewContentAlignment.MiddleLeft, NullValue = "—" };
        grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#FAFBFC"); grid.GridColor = Theme.Border;
        AddTextColumn(grid, "Index", "序号", 7, 48, DataGridViewContentAlignment.MiddleCenter); AddTextColumn(grid, "No", "报关单编号", 19, 200); AddTextColumn(grid, "Consignee", "境外收货人", 17, 155); AddTextColumn(grid, "Contract", "合同协议号", 13, 120); AddTextColumn(grid, "Customs", "出境关别", 11, 84); AddTextColumn(grid, "Country", "目的国", 10, 70); AddTextColumn(grid, "Total", "关单总货值", 16, 170, DataGridViewContentAlignment.MiddleRight); AddTextColumn(grid, "Status", "状态", 10, 106, DataGridViewContentAlignment.MiddleCenter);
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Verify", HeaderText = "网页核验", Text = "核验", UseColumnTextForButtonValue = false, FillWeight = 8, MinimumWidth = 90, Resizable = DataGridViewTriState.True, FlatStyle = FlatStyle.Flat });
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns["No"].DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.Columns["Total"].DefaultCellStyle.Font = Theme.MonoFont(14F);
        grid.Columns["Consignee"].DefaultCellStyle.Font = Theme.UiFont(13F);
        grid.CellPainting += PaintRecordCell;
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
        var label = new Label { Text = "暂无关单记录", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, Font = Theme.UiFont(14F), Size = new Size(540, 54), AccessibleName = "暂无关单记录，可拖入文件识别" };
        void CenterContent()
        {
            var left = Math.Max(0, (panel.ClientSize.Width - label.Width) / 2);
            var top = Math.Max(8, (panel.ClientSize.Height - 92) / 2);
            icon.Location = new Point((panel.ClientSize.Width - icon.Width) / 2, top);
            label.Location = new Point(left, top + 52);
        }
        _emptyLabel = label; panel.Controls.AddRange([icon, label]); panel.Resize += (_, _) => CenterContent(); CenterContent();
        return panel;
    }

    private Control BuildPagination(out Button previous, out Label pageLabel, out Button next, out Label footer)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 9, 24, 9), ColumnCount = 4, RowCount = 1, BackColor = Theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        footer = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.UiFont(14F), AutoEllipsis = true };
        previous = Theme.SecondaryButton("上一页"); previous.Dock = DockStyle.Fill; previous.Margin = new Padding(0, 0, 8, 0);
        pageLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text, Font = Theme.UiFont(14F) };
        next = Theme.SecondaryButton("下一页"); next.Dock = DockStyle.Fill; next.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(footer, 0, 0); layout.Controls.Add(previous, 1, 0); layout.Controls.Add(pageLabel, 2, 0); layout.Controls.Add(next, 3, 0); return layout;
    }

}
