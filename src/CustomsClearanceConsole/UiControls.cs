using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal sealed class RoundedButton : Button
{
    /// <summary>Corner radius in 96-DPI logical pixels; drawn at the control's real DPI.</summary>
    public int Radius { get; set; } = 7;
    private float ScaledRadius => Radius * DeviceDpi / 96F;
    public Color BorderColor { get; set; } = Theme.Border;
    public Color HoverBackColor { get; set; } = Color.White;
    public Color PressedBackColor { get; set; } = Color.White;
    public Color DisabledFill { get; set; } = Theme.DisabledFill;
    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Space or Keys.Enter) { _pressed = true; Invalidate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode is Keys.Space or Keys.Enter) { _pressed = false; Invalidate(); } }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var back = _pressed ? PressedBackColor : _hover ? HoverBackColor : BackColor;
        var parentBack = Parent?.BackColor ?? Color.White;
        if (!Enabled) back = DisabledFill;
        var fore = Enabled ? ForeColor : Theme.Blend(ForeColor, parentBack, .58F);
        var border = Enabled ? BorderColor : Theme.Blend(BorderColor, parentBack, .58F);
        using var path = Theme.RoundedPath(new RectangleF(.5F, .5F, Math.Max(1, Width - 1F), Math.Max(1, Height - 1F)), ScaledRadius);
        using (var brush = new SolidBrush(back)) e.Graphics.FillPath(brush, path);
        using (var pen = new Pen(border, 1F)) e.Graphics.DrawPath(pen, path);

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var textSize = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, Height), flags);
        var image = Image;
        var gap = image is null || string.IsNullOrEmpty(Text) ? 0 : 10;
        var imageWidth = image?.Width ?? 0;
        var contentWidth = imageWidth + gap + textSize.Width;
        var startX = Math.Max(0, (Width - contentWidth) / 2);
        if (image is not null)
        {
            var imageY = (Height - image.Height) / 2;
            if (Enabled) e.Graphics.DrawImage(image, startX, imageY, image.Width, image.Height);
            else ControlPaint.DrawImageDisabled(e.Graphics, image, startX, imageY, parentBack);
            startX += imageWidth + gap;
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(startX, 0, Math.Max(0, Width - startX), Height), fore, flags | TextFormatFlags.Left);
        if (Focused && ShowFocusCues)
        {
            using var focusPath = Theme.RoundedPath(new RectangleF(3F, 3F, Math.Max(1, Width - 6F), Math.Max(1, Height - 6F)), Math.Max(2, ScaledRadius - 2));
            using var focusPen = new Pen(Theme.Blue, 2F);
            e.Graphics.DrawPath(focusPen, focusPath);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), ScaledRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }
}

internal class RoundedPanel : Panel
{
    /// <summary>Corner radius in 96-DPI logical pixels; drawn at the control's real DPI.</summary>
    public int Radius { get; set; } = 8;
    private float ScaledRadius => Radius * DeviceDpi / 96F;
    public Color BorderColor { get; set; } = Theme.Border;
    public int BorderWidth { get; set; } = 1;
    public Color AccentColor { get; set; } = Color.Transparent;
    public int AccentHeight { get; set; }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), ScaledRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var borderPath = Theme.RoundedPath(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), ScaledRadius);
        using var borderPen = new Pen(BorderColor, BorderWidth);
        eventArgs.Graphics.DrawPath(borderPen, borderPath);
        if (AccentHeight > 0 && AccentColor != Color.Transparent)
        {
            using var accentPen = new Pen(AccentColor, AccentHeight);
            eventArgs.Graphics.DrawLine(accentPen, ScaledRadius, AccentHeight / 2F, Width - ScaledRadius, AccentHeight / 2F);
        }
    }
}

internal enum CaptionGlyph { Minimize, Maximize, Close }

internal sealed class WindowCaptionButton : Control
{
    private readonly CaptionGlyph _glyph;
    private bool _hover;
    private bool _pressed;
    internal bool ShowsRestoreGlyph => _glyph == CaptionGlyph.Maximize && FindForm()?.WindowState == FormWindowState.Maximized;

    public WindowCaptionButton(CaptionGlyph glyph)
    {
        _glyph = glyph;
        Size = new Size(46, UiTokens.Metrics.Header);
        Cursor = Cursors.Default;
        TabStop = false;
        AccessibleName = glyph switch { CaptionGlyph.Minimize => "最小化", CaptionGlyph.Maximize => "最大化", _ => "关闭" };
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    public void RefreshWindowState()
    {
        if (_glyph == CaptionGlyph.Maximize)
            AccessibleName = FindForm()?.WindowState == FormWindowState.Maximized ? "还原窗口" : "最大化";
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    /// <summary>
    /// Windows 11 caption button: full title-bar height, 46 logical pixels wide, a 10-pixel
    /// glyph drawn crisply at the real DPI, the title-bar colour at rest, a light overlay on
    /// hover and the system red behind the close glyph.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var restColor = Parent?.BackColor ?? Theme.HeaderBg;
        var back = !_hover ? restColor
            : _glyph == CaptionGlyph.Close ? (_pressed ? ColorTranslator.FromHtml("#94251A") : ColorTranslator.FromHtml("#C42B1C"))
            : Blend(restColor, Color.White, _pressed ? 0.16F : 0.10F);
        e.Graphics.Clear(back);
        var scale = DeviceDpi / 96F;
        var stroke = Math.Max(1F, MathF.Round(scale));
        var half = MathF.Round(5F * scale);
        var cx = MathF.Round(ClientSize.Width / 2F);
        var cy = MathF.Round(ClientSize.Height / 2F);
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        if (_glyph == CaptionGlyph.Close)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.White, stroke * 1.1F);
            e.Graphics.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
            e.Graphics.DrawLine(pen, cx + half, cy - half, cx - half, cy + half);
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.None;
        using var crisp = new Pen(Color.White, stroke) { LineJoin = LineJoin.Miter };
        if (_glyph == CaptionGlyph.Minimize)
        {
            e.Graphics.DrawLine(crisp, cx - half, cy, cx + half, cy);
            return;
        }
        var restore = ShowsRestoreGlyph;
        AccessibleName = restore ? "还原窗口" : "最大化";
        if (!restore)
        {
            e.Graphics.DrawRectangle(crisp, cx - half, cy - half, half * 2, half * 2);
            return;
        }
        // Restore: a front square plus the visible top/right edges of the square behind it.
        var offset = MathF.Round(2F * scale);
        var size = half * 2 - offset;
        var frontLeft = cx - half;
        var frontTop = cy - half + offset;
        e.Graphics.DrawRectangle(crisp, frontLeft, frontTop, size, size);
        var backLeft = frontLeft + offset;
        var backTop = cy - half;
        var backRight = backLeft + size;
        e.Graphics.DrawLine(crisp, backLeft, backTop, backRight, backTop);
        e.Graphics.DrawLine(crisp, backRight, backTop, backRight, backTop + size);
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));
}

internal sealed class SearchField : RoundedPanel
{
    public SearchField(TextBox textBox)
    {
        // Design 2.5: height 34, 1px borderControl, radius 6, 16px search-muted icon.
        Radius = 6;
        BorderColor = Theme.BorderControl;
        BackColor = Color.White;
        // Logical pixels; the window scales them to its DPI. Icon column 0-36, 7px vertical
        // breathing room so the 13px input text is not clipped (R4-2).
        Padding = new Padding(36, 7, 12, 6);
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0);
        textBox.Font = Theme.UiFont(13);
        textBox.GotFocus += (_, _) => { BorderColor = Theme.BlueHover; BorderWidth = 2; Invalidate(); };
        textBox.LostFocus += (_, _) => { BorderColor = Theme.BorderControl; BorderWidth = 1; Invalidate(); };
        Click += (_, _) => textBox.Focus();
        Controls.Add(textBox);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var icon = UiV2Icons.Load(Ui2.Search, 16, DeviceDpi);
        if (icon is null) return;
        var x = (int)Math.Round(12 * DeviceDpi / 96F);
        e.Graphics.DrawImage(icon, x, (Height - icon.Height) / 2, icon.Width, icon.Height);
    }
}

internal sealed class ModernDropDown : RoundedPanel
{
    public List<string> Items { get; } = [];
    public int ItemHeight { get; set; } = 44;
    public bool OpenUpward { get; set; }
    public bool UseMonoValue { get; set; }
    private int _selectedIndex = -1;
    private bool _open;
    private DropDownPopupForm? _popup;
    private DropDownOptionsPanel? _options;
    public event EventHandler? SelectedIndexChanged;

    public ModernDropDown()
    {
        Radius = 7;
        BorderColor = Theme.Border;
        BackColor = Color.White;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = value >= 0 && value < Items.Count ? value : -1;
            if (_selectedIndex == next) return;
            _selectedIndex = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem
    {
        get => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
        set => SelectedIndex = value is null ? -1 : Items.FindIndex(x => string.Equals(x, value, StringComparison.Ordinal));
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); ShowOptions(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { ShowOptions(); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); BorderColor = Theme.BlueHover; BorderWidth = 2; Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (!_open) { BorderColor = Theme.Border; BorderWidth = 1; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var text = SelectedItem ?? string.Empty;
        var textRect = new Rectangle(ScaleX(10), 0, Math.Max(0, Width - ScaleX(42)), Height);
        using var textFont = UseMonoValue ? Theme.MonoFont(13F, true) : Theme.UiFont(13F, FontStyle.Bold);
        var textColor = Enabled ? Theme.FieldText : Theme.DisabledText;
        TextRenderer.DrawText(e.Graphics, text, textFont, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Enabled ? Theme.Muted : Theme.DisabledText, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = Width - ScaleX(15F); var y = Height / 2F;
        if (_open) { e.Graphics.DrawLine(pen, x - 5, y + 2.5F, x, y - 2.5F); e.Graphics.DrawLine(pen, x, y - 2.5F, x + 5, y + 2.5F); }
        else { e.Graphics.DrawLine(pen, x - 5, y - 2.5F, x, y + 2.5F); e.Graphics.DrawLine(pen, x, y + 2.5F, x + 5, y - 2.5F); }
    }

    private int ScaleX(int value) => (int)Math.Round(value * DeviceDpi / 96F);
    private int ScaleX(float value) => (int)Math.Round(value * DeviceDpi / 96F);

    private void ShowOptions()
    {
        if (_open || Items.Count == 0 || !Enabled) return;
        Focus();
        _open = true; BorderColor = Theme.Accent; BorderWidth = 2; Invalidate();
        EnsurePopup();
        if (_popup is null || _options is null) return;
        var itemHeight = ScaleX(ItemHeight);
        _options.Size = new Size(Width, Items.Count * itemHeight + ScaleX(8));
        _options.ResetHover();
        _popup.ClientSize = _options.Size;
        var owner = FindForm();
        _popup.Opacity = owner?.Opacity ?? 1D;
        var working = Screen.FromControl(this).WorkingArea;
        var desired = PointToScreen(new Point(0, Height + ScaleX(6)));
        var x = OpenUpward ? PointToScreen(new Point(Width - _popup.Width, 0)).X : desired.X;
        x = Math.Clamp(x, working.Left, Math.Max(working.Left, working.Right - _popup.Width));
        int y;
        if (OpenUpward)
            y = Math.Max(working.Top, PointToScreen(Point.Empty).Y - _popup.Height - ScaleX(6));
        else
            y = desired.Y + _popup.Height <= working.Bottom
                ? desired.Y
                : Math.Max(working.Top, PointToScreen(Point.Empty).Y - _popup.Height - ScaleX(6));
        _popup.Location = new Point(x, y);
        if (owner is not null) _popup.Show(owner); else _popup.Show();
        _popup.Activate();
    }

    private void EnsurePopup()
    {
        if (_popup is { IsDisposed: false }) return;
        _options = new DropDownOptionsPanel(Items, () => SelectedIndex, index => SelectedIndex = index, () => ScaleX(ItemHeight))
        { Size = new Size(Width, Items.Count * ScaleX(ItemHeight) + ScaleX(8)) };
        _popup = new DropDownPopupForm(_options);
        _popup.Deactivate += (_, _) => HidePopup();
        _popup.VisibleChanged += (_, _) => { if (!_popup.Visible) SetClosedState(); };
        _options.OptionChosen += (_, _) => { HidePopup(); Focus(); };
    }

    private void HidePopup()
    {
        if (_popup is { IsDisposed: false, Visible: true }) _popup.Hide();
        SetClosedState();
    }

    private void SetClosedState()
    {
        if (!_open) return;
        _open = false;
        BorderColor = Focused ? Theme.BlueHover : Theme.Border;
        BorderWidth = Focused ? 2 : 1;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_popup is { IsDisposed: false })
            {
                if (_popup.Visible) _popup.Hide();
                _popup.Dispose();
            }
            _popup = null;
            _options = null;
        }
        base.Dispose(disposing);
    }

    private sealed class DropDownPopupForm : Form
    {
        public DropDownPopupForm(Control content)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Padding = Padding.Empty;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                var parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 10F * DeviceDpi / 96F);
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }
    }

    private sealed class DropDownOptionsPanel : Control
    {
        private readonly IReadOnlyList<string> _items;
        private readonly Func<int> _selected;
        private readonly Action<int> _choose;
        private readonly Func<int> _itemHeight;
        private int _hover = -1;
        public event EventHandler? OptionChosen;

        public DropDownOptionsPanel(IReadOnlyList<string> items, Func<int> selected, Action<int> choose, Func<int> itemHeight)
        {
            _items = items; _selected = selected; _choose = choose; _itemHeight = itemHeight;
            BackColor = Color.White; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void ResetHover() { _hover = -1; Invalidate(); }

        private int IndexAt(int y)
        {
            var pad = (int)Math.Round(4 * DeviceDpi / 96F);
            var height = Math.Max(1, _itemHeight());
            var index = (y - pad) / height;
            return index < 0 || index >= _items.Count ? -1 : index;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var next = IndexAt(e.Y); if (next != _hover) { _hover = next; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); var index = IndexAt(e.Y);
            if (index < 0) return;
            _choose(index); OptionChosen?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var dpi = DeviceDpi / 96F;
            int S(int px) => (int)Math.Round(px * dpi);
            float Sf(float px) => (float)Math.Round(px * dpi);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Color.White);
            var pad = S(4);
            var height = Math.Max(1, _itemHeight());
            for (var i = 0; i < _items.Count; i++)
            {
                var row = new Rectangle(pad, pad + i * height, Width - pad * 2, height);
                if (i == _selected() || i == _hover)
                {
                    using var path = Theme.RoundedPath(row, Sf(5));
                    using var brush = new SolidBrush(i == _selected() ? Theme.AccentSoft : Theme.SegmentBg);
                    e.Graphics.FillPath(brush, path);
                }
                using var itemFont = Theme.UiFont(13.5F, i == _selected() ? FontStyle.Bold : FontStyle.Regular);
                TextRenderer.DrawText(e.Graphics, _items[i], itemFont, new Rectangle(row.X + S(20), row.Y, row.Width - S(44), row.Height),
                    i == _selected() ? Theme.Primary : Theme.FieldText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (i == _selected())
                {
                    var check = UiV2Icons.Load(Ui2.CheckBlue, 16, DeviceDpi);
                    if (check is not null)
                    {
                        e.Graphics.DrawImage(check, row.X + S(5), row.Y + (row.Height - check.Height) / 2, check.Width, check.Height);
                        check.Dispose();
                    }
                }
            }
            using var border = new Pen(Theme.Border);
            using var borderPath = Theme.RoundedPath(new RectangleF(.5F, .5F, Width - 1, Height - 1), Sf(8));
            e.Graphics.DrawPath(border, borderPath);
        }
    }
}

internal sealed class OutlinedField : RoundedPanel
{
    public OutlinedField(Control control)
    {
        Radius = 7;
        BorderColor = Theme.Border;
        BackColor = Color.White;
        Padding = new Padding(12, 5, 8, 5);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0);
        Controls.Add(control);
    }
}

/// <summary>
/// Current page number in the pager (design 2.5 footer): a 28px rounded square in the
/// primary colour with white text, grey when there is nothing to page through.
/// </summary>
internal sealed class PageBadge : Label
{
    public PageBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        var scale = DeviceDpi / 96F;
        var side = (int)Math.Round(28 * scale);
        var textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, side), TextFormatFlags.NoPadding).Width;
        var width = Math.Min(ClientSize.Width, Math.Max(side, textWidth + (int)Math.Round(16 * scale)));
        var badge = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - side) / 2, width, side);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(badge, 4 * scale))
        using (var fill = new SolidBrush(Enabled ? Theme.Primary : Theme.DisabledFill))
            e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, badge, Enabled ? Color.White : Theme.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
