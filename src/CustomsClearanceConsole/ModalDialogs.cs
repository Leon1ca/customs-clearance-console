using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal static class ModalPresenter
{
    public static DialogResult Show(Form dialog, Form owner)
    {
        using var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = owner.Bounds,
            BackColor = ColorTranslator.FromHtml("#0E1C32"),
            Opacity = .46,
            AutoScaleMode = AutoScaleMode.None
        };
        backdrop.Show(owner);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(
            backdrop.Left + Math.Max(0, (backdrop.Width - dialog.Width) / 2),
            backdrop.Top + Math.Max(0, (backdrop.Height - dialog.Height) / 2));
        try { return dialog.ShowDialog(backdrop); }
        finally { backdrop.Close(); }
    }
}

internal sealed class ConfirmationDialog : Form
{
    private readonly Button _cancel;

    internal ConfirmationDialog(string titleText, string bodyText, string warningText, string confirmText = "确认清理")
    {
        Text = titleText;
        ClientSize = new Size(640, 414);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var frame = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 12,
            BorderColor = ColorTranslator.FromHtml("#AEBED1"),
            BackColor = Color.White,
            Margin = new Padding(0)
        };

        Control icon = confirmText == "确认清理"
            ? new DangerTrashIcon { Location = new Point(32, 30), Size = new Size(52, 52) }
            : new Label { Text = "✓", Location = new Point(32, 30), Size = new Size(52, 52), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Blue, Font = Theme.UiFont(28) };
        var title = new Label
        {
            Text = titleText,
            Location = new Point(102, 30),
            Size = new Size(506, 52),
            Font = Theme.UiFont(22F, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var body = new Label
        {
            Text = bodyText,
            Location = new Point(32, 106),
            Size = new Size(576, 64),
            Font = Theme.UiFont(14F),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var warning = new RoundedPanel
        {
            Location = new Point(32, 184),
            Size = new Size(576, 112),
            Radius = 7,
            BorderColor = ColorTranslator.FromHtml("#FFF5F3"),
            BackColor = ColorTranslator.FromHtml("#FFF5F3")
        };
        warning.Controls.Add(new TextBox
        {
            Text = warningText, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Theme.DangerSoft, ForeColor = Theme.Text, Font = Theme.UiFont(14),
            AccessibleName = "完整范围和目录"
        });
        warning.Padding = new Padding(14);


        var footer = new Panel { Location = new Point(1, 322), Size = new Size(638, 91), BackColor = Color.White };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(ColorTranslator.FromHtml("#D5DEE9"));
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        _cancel = Theme.SecondaryButton("取消");
        _cancel.Location = new Point(371, 20);
        _cancel.Size = new Size(112, 48);
        _cancel.DialogResult = DialogResult.No;
        var confirm = confirmText == "确认清理" ? Theme.DangerSolidButton(confirmText) : Theme.PrimaryButton(confirmText);
        confirm.Location = new Point(495, 20);
        confirm.Size = new Size(112, 48);
        confirm.DialogResult = DialogResult.Yes;
        footer.Controls.AddRange([_cancel, confirm]);

        frame.Controls.AddRange([icon, title, body, warning, footer]);
        Controls.Add(frame);
        AcceptButton = _cancel;
        CancelButton = _cancel;
    }

    public static bool Confirm(Form owner, string title, string body, string warning, string confirmText = "确认清理")
    {
        using var dialog = new ConfirmationDialog(title, body, warning, confirmText);
        return ModalPresenter.Show(dialog, owner) == DialogResult.Yes;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = _cancel;
        _cancel.Focus();
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

internal sealed class DangerTrashIcon : Control
{
    public DangerTrashIcon()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        AccessibleName = "清理警告";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(ColorTranslator.FromHtml("#FFDAD6"))) e.Graphics.FillEllipse(back, 0, 0, Width - 1, Height - 1);
        var scale = Math.Min(Width, Height) / 52F;
        e.Graphics.TranslateTransform(14F * scale, 14F * scale);
        e.Graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(Theme.Danger, 1.9F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLine(pen, 2, 5, 22, 5);
        e.Graphics.DrawLines(pen, [new PointF(7, 5), new PointF(8, 21), new PointF(16, 21), new PointF(17, 5)]);
        e.Graphics.DrawLines(pen, [new PointF(8, 5), new PointF(9, 1), new PointF(15, 1), new PointF(16, 5)]);
        e.Graphics.DrawLine(pen, 10, 9, 10.5F, 17);
        e.Graphics.DrawLine(pen, 14, 9, 13.5F, 17);
    }
}

internal sealed class CircleCloseButton : Control
{
    private bool _hover;

    public CircleCloseButton()
    {
        Size = new Size(44, 44);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "关闭目录设置";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(_hover ? ColorTranslator.FromHtml("#E7ECF3") : ColorTranslator.FromHtml("#EEF2F7")))
            e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        using var pen = new Pen(Theme.Muted, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var cx = Width / 2F; var cy = Height / 2F;
        e.Graphics.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
        e.Graphics.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(Theme.Blue, 2F);
            e.Graphics.DrawEllipse(focusPen, 2, 2, Width - 5, Height - 5);
        }
    }
}
