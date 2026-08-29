namespace CustomsClearanceConsole;

internal sealed class DeclarationDetailsForm : Form
{
    private readonly Button _close;

    public DeclarationDetailsForm(DeclarationRecord record)
    {
        Text = "关单金额详情";
        ClientSize = new Size(920, 620);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;

        var frame = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 12,
            BorderColor = ColorTranslator.FromHtml("#AEBED1"),
            BackColor = Color.White,
            Margin = new Padding(0)
        };

        var header = new Panel
        {
            Location = new Point(1, 1),
            Size = new Size(918, 112),
            BackColor = ColorTranslator.FromHtml("#F6F8FB")
        };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ColorTranslator.FromHtml("#D5DEE9"));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        header.Controls.Add(new Label
        {
            Text = "关单金额详情",
            Location = new Point(32, 22),
            Size = new Size(540, 38),
            Font = Theme.UiFont(26F, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft
        });
        header.Controls.Add(new Label
        {
            Text = $"报关单号：{EmptyAsDash(record.DeclarationNo)}    源文件：{record.SourceName}",
            Location = new Point(33, 66),
            Size = new Size(790, 25),
            Font = Theme.UiFont(15F),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });
        var closeCircle = new CircleCloseButton { Location = new Point(842, 30), AccessibleName = "关闭金额详情" };
        closeCircle.Click += (_, _) => Close();
        header.Controls.Add(closeCircle);

        var grid = BuildGrid();
        grid.Location = new Point(32, 141);
        grid.Size = new Size(856, 366);
        foreach (var line in record.LineTotals.OrderBy(x => x.Sequence))
        {
            var verification = line.VerificationAmount is null ? "—" : $"{line.Currency} {line.VerificationAmount.Value:N2}";
            var status = line.IsReliable
                ? line.Note.Contains("结构校验", StringComparison.Ordinal) ? "结构校验通过" : line.VerificationAmount is null ? "已识别" : "双引擎一致"
                : string.IsNullOrWhiteSpace(line.Note) ? "需复核" : line.Note;
            var rowIndex = grid.Rows.Add(
                line.Sequence,
                $"第 {Math.Max(1, line.PageNumber)} 页",
                string.IsNullOrWhiteSpace(line.ItemNo) ? "—" : line.ItemNo,
                line.DisplayAmount,
                verification,
                status);
            if (!line.IsReliable)
            {
                grid.Rows[rowIndex].DefaultCellStyle.BackColor = Theme.DangerSoft;
                grid.Rows[rowIndex].DefaultCellStyle.ForeColor = Theme.Danger;
            }
            foreach (DataGridViewCell cell in grid.Rows[rowIndex].Cells)
                cell.ToolTipText = string.IsNullOrWhiteSpace(line.Note) ? Convert.ToString(cell.FormattedValue) ?? "" : line.Note;
        }

        var summary = new RoundedPanel
        {
            Location = new Point(32, 525),
            Size = new Size(654, 62),
            Radius = 7,
            BorderColor = ColorTranslator.FromHtml("#D5DEE9"),
            BackColor = ColorTranslator.FromHtml("#F7F9FC")
        };
        var reliableCount = record.LineTotals.Count(x => x.IsReliable);
        var attentionCount = record.LineTotals.Count - reliableCount;
        summary.Controls.Add(new Label
        {
            Text = record.LineTotals.Count == 0
                ? "未识别到逐项总价，请对照原单人工核查。"
                : $"识别 {record.LineTotals.Count} 条 · 计入合计 {reliableCount} 条{(attentionCount > 0 ? $" · 待复核 {attentionCount} 条" : "")}    合计：{record.DisplayTotal}",
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 0, 18, 0),
            Font = Theme.UiFont(16F),
            ForeColor = attentionCount > 0 || record.LineTotals.Count == 0 ? Theme.Warning : Theme.FieldText,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });

        _close = Theme.PrimaryButton("关闭");
        _close.Location = new Point(708, 532);
        _close.Size = new Size(180, 48);
        _close.DialogResult = DialogResult.OK;
        frame.Controls.AddRange([header, grid, summary, _close]);
        Controls.Add(frame);
        AcceptButton = _close;
        CancelButton = _close;
    }

    private static DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = true,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 52,
            RowTemplate = { Height = 48 },
            GridColor = Theme.Border
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.HeaderSoft,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(16F, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleCenter,
            SelectionBackColor = Theme.HeaderSoft,
            SelectionForeColor = Theme.Text
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(15F),
            SelectionBackColor = ColorTranslator.FromHtml("#DCE8F8"),
            SelectionForeColor = Theme.Text,
            Padding = new Padding(8, 0, 8, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#F8FAFD");
        AddColumn(grid, "Sequence", "序号", 9, 58, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "Page", "PDF 页码", 13, 92, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "Item", "项号", 10, 72, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(grid, "Primary", "识别总价", 20, 140, DataGridViewContentAlignment.MiddleRight);
        AddColumn(grid, "Verification", "复核金额", 20, 140, DataGridViewContentAlignment.MiddleRight);
        AddColumn(grid, "Status", "校对结果", 28, 190);
        return grid;
    }

    private static void AddColumn(DataGridView grid, string name, string header, float weight, int minimumWidth,
        DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = weight,
            MinimumWidth = minimumWidth,
            Resizable = DataGridViewTriState.True,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        });

    private static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = _close;
        _close.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 12);
        Region = new Region(path);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int csDropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= csDropShadow;
            return parameters;
        }
    }
}
