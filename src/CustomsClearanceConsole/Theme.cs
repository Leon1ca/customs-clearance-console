using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal static class Theme
{
    public static readonly Color Navy = ColorTranslator.FromHtml("#15345B");
    public static readonly Color Blue = ColorTranslator.FromHtml("#2563EB");
    public static readonly Color BlueHover = ColorTranslator.FromHtml("#1D4ED8");
    public static readonly Color Surface = Color.White;
    public static readonly Color Canvas = ColorTranslator.FromHtml("#F4F7FB");
    public static readonly Color Border = ColorTranslator.FromHtml("#D9E2EC");
    public static readonly Color Text = ColorTranslator.FromHtml("#172033");
    public static readonly Color Muted = ColorTranslator.FromHtml("#667085");
    public static readonly Color Danger = ColorTranslator.FromHtml("#D92D20");
    public static readonly Color DangerSoft = ColorTranslator.FromHtml("#FFF1F0");
    public static readonly Color Success = ColorTranslator.FromHtml("#138A5B");
    public static readonly Color Warning = ColorTranslator.FromHtml("#B54708");

    public static Button PrimaryButton(string text) => Button(text, Blue, Color.White, Blue);
    public static Button SecondaryButton(string text) => Button(text, Color.White, Text, Border);
    public static Button DangerButton(string text) => Button(text, Color.White, Danger, ColorTranslator.FromHtml("#F5B7B1"));

    private static Button Button(string text, Color back, Color fore, Color border)
    {
        var button = new Button
        {
            Text = text, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular),
            UseVisualStyleBackColor = false, TabStop = true
        };
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = back == Blue ? BlueHover : ColorTranslator.FromHtml("#F8FAFC");
        return button;
    }
}

internal sealed class MetricCard : Panel
{
    private readonly Label _value;
    private readonly Label _hint;
    private readonly ToolTip _toolTip;

    public MetricCard(string title, string value, string hint, Color accent, float valueFontSize = 18F)
    {
        BackColor = Color.White;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 2, RowCount = 3,
            Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 5));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = accent, Margin = new Padding(0, 0, 0, 2) };
        var titleLabel = new Label
        {
            Text = title, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 9F),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 0, 0, 0)
        };
        _value = new Label
        {
            Text = value, ForeColor = Theme.Text, Font = new Font("Microsoft YaHei UI", valueFontSize, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = false,
            Margin = new Padding(10, 0, 0, 0), UseCompatibleTextRendering = false
        };
        _hint = new Label
        {
            Text = hint, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 8.5F),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true,
            Margin = new Padding(10, 0, 0, 0), UseCompatibleTextRendering = false
        };
        layout.Controls.Add(bar, 0, 0); layout.SetRowSpan(bar, 3);
        layout.Controls.Add(titleLabel, 1, 0); layout.Controls.Add(_value, 1, 1); layout.Controls.Add(_hint, 1, 2);
        _toolTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };
        _toolTip.SetToolTip(_value, value); _toolTip.SetToolTip(_hint, hint);
        Controls.Add(layout);
        Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Theme.Border, ButtonBorderStyle.Solid);
    }

    public void Set(string value, string hint)
    {
        _value.Text = value; _hint.Text = hint;
        _value.AccessibleDescription = value; _hint.AccessibleDescription = hint;
        _toolTip.SetToolTip(_value, value); _toolTip.SetToolTip(_hint, hint);
        _value.Invalidate(); _hint.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ProgressDialog : Form
{
    private readonly Label _label;
    private readonly ProgressBar _progress;
    public CancellationTokenSource Cancellation { get; } = new();

    public ProgressDialog()
    {
        Text = "正在识别关单"; Size = new Size(520, 190); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ControlBox = false;
        BackColor = Color.White; Font = new Font("Microsoft YaHei UI", 10F);
        _label = new Label { Text = "准备读取文件……", Location = new Point(28, 26), Size = new Size(450, 44), ForeColor = Theme.Text };
        _progress = new ProgressBar { Location = new Point(28, 79), Size = new Size(450, 17), Style = ProgressBarStyle.Continuous };
        var cancel = Theme.SecondaryButton("取消"); cancel.Size = new Size(100, 36); cancel.Location = new Point(378, 112); cancel.Click += (_, _) => Cancellation.Cancel();
        Controls.AddRange([_label, _progress, cancel]);
    }

    public void UpdateProgress(int done, int total, string file)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateProgress(done, total, file)); return; }
        _progress.Maximum = Math.Max(1, total); _progress.Value = Math.Min(done, _progress.Maximum);
        _label.Text = $"{done}/{total}  {file}";
    }
}
