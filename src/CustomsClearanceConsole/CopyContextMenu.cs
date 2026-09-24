using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal sealed class CopyContextMenu : ToolStripDropDown
{
    private readonly CopyMenuItemControl _content;

    public CopyContextMenu(Action copy)
    {
        AutoClose = true;
        AutoSize = false;
        BackColor = Color.White;
        DropShadowEnabled = true;
        Padding = new Padding(4);
        Size = new Size(224, 56);
        _content = new CopyMenuItemControl(() => { copy(); Close(); }) { Size = new Size(214, 46) };
        Items.Add(new ToolStripControlHost(_content)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = _content.Size
        });
        Opened += (_, _) => _content.Focus();
        UpdateRegion();
    }

    public void ShowAt(Point screenLocation, bool enabled)
    {
        _content.Enabled = enabled;
        Show(screenLocation);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 8F);
        using var brush = new SolidBrush(Color.White);
        using var pen = new Pen(ColorTranslator.FromHtml("#C2C7CF"));
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 8F);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    private sealed class CopyMenuItemControl : Control
    {
        private readonly Action _copy;
        private bool _hover;

        public CopyMenuItemControl(Action copy)
        {
            _copy = copy;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            Font = Theme.UiFont(14F);
            TabStop = true;
            AccessibleRole = AccessibleRole.MenuItem;
            AccessibleName = "复制选中的内容";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) _copy(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                PerformCopy();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void PerformCopy()
        {
            if (Enabled) _copy();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var surface = _hover || Focused ? ColorTranslator.FromHtml("#F0F4F9") : Color.White;
            if (!Enabled) surface = Color.White;
            using (var path = Theme.RoundedPath(new RectangleF(0, 0, Width - 1F, Height - 1F), 6F))
            using (var brush = new SolidBrush(surface)) e.Graphics.FillPath(brush, path);

            var foreground = Enabled ? Theme.FieldText : Theme.Placeholder;
            var shortcut = Enabled ? Theme.Placeholder : ColorTranslator.FromHtml("#A8B0BC");
            TextRenderer.DrawText(e.Graphics, "复制选中的内容", Font,
                new Rectangle(14, 0, Width - 92, Height), foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            using (var shortcutFont = Theme.UiFont(12F))
                TextRenderer.DrawText(e.Graphics, "Ctrl+C", shortcutFont,
                    new Rectangle(Width - 76, 0, 62, Height), shortcut,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Theme.Blue, 1F) { DashStyle = DashStyle.Dot };
                e.Graphics.DrawRectangle(pen, 4, 4, Width - 9, Height - 9);
            }
        }
    }
}
