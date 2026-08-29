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

    internal ConfirmationDialog(string titleText, string bodyText, string warningText)
    {
        Text = titleText;
        ClientSize = new Size(640, 324);
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

        var icon = new DangerTrashIcon { Location = new Point(32, 30), Size = new Size(52, 52) };
        var title = new Label
        {
            Text = titleText,
            Location = new Point(102, 30),
            Size = new Size(506, 52),
            Font = Theme.UiFont(26F, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var body = new Label
        {
            Text = bodyText,
            Location = new Point(32, 106),
            Size = new Size(576, 26),
            Font = Theme.UiFont(17F),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var warning = new RoundedPanel
        {
            Location = new Point(32, 148),
            Size = new Size(576, 58),
            Radius = 7,
            BorderColor = ColorTranslator.FromHtml("#FFF5F3"),
            BackColor = ColorTranslator.FromHtml("#FFF5F3")
        };
        warning.Controls.Add(new Label
        {
            Text = warningText,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 0, 18, 0),
            Font = Theme.UiFont(16F),
            ForeColor = ColorTranslator.FromHtml("#8C1D18"),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });

        var footer = new Panel { Location = new Point(1, 232), Size = new Size(638, 91), BackColor = Color.White };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(ColorTranslator.FromHtml("#D5DEE9"));
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        _cancel = Theme.SecondaryButton("取消");
        _cancel.Location = new Point(371, 20);
        _cancel.Size = new Size(112, 48);
        _cancel.DialogResult = DialogResult.No;
        var confirm = Theme.DangerSolidButton("确认清理");
        confirm.Location = new Point(495, 20);
        confirm.Size = new Size(112, 48);
        confirm.DialogResult = DialogResult.Yes;
        footer.Controls.AddRange([_cancel, confirm]);

        frame.Controls.AddRange([icon, title, body, warning, footer]);
        Controls.Add(frame);
        AcceptButton = _cancel;
        CancelButton = _cancel;
    }

    public static bool Confirm(Form owner, string title, string body, string warning)
    {
        using var dialog = new ConfirmationDialog(title, body, warning);
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
