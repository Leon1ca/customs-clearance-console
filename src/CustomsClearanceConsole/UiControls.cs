using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal sealed class RoundedButton : Button
{
    public int Radius { get; set; } = 7;
    public Color BorderColor { get; set; } = Theme.Border;
    public Color HoverBackColor { get; set; } = Color.White;
    public Color PressedBackColor { get; set; } = Color.White;
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
        if (!Enabled) back = Theme.Blend(back, parentBack, .58F);
        var fore = Enabled ? ForeColor : Theme.Blend(ForeColor, parentBack, .58F);
        var border = Enabled ? BorderColor : Theme.Blend(BorderColor, parentBack, .58F);
        using var path = Theme.RoundedPath(new RectangleF(.5F, .5F, Math.Max(1, Width - 1F), Math.Max(1, Height - 1F)), Radius);
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
            using var focusPath = Theme.RoundedPath(new RectangleF(3F, 3F, Math.Max(1, Width - 6F), Math.Max(1, Height - 6F)), Math.Max(2, Radius - 2));
            using var focusPen = new Pen(Theme.Blue, 2F);
            e.Graphics.DrawPath(focusPen, focusPath);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), Radius);
        Region = new Region(path);
    }
}

internal class RoundedPanel : Panel
{
    public int Radius { get; set; } = 8;
    public Color BorderColor { get; set; } = Theme.Border;
    public int BorderWidth { get; set; } = 1;
    public Color AccentColor { get; set; } = Color.Transparent;
    public int AccentHeight { get; set; }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var borderPath = Theme.RoundedPath(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), Radius);
        using var borderPen = new Pen(BorderColor, BorderWidth);
        eventArgs.Graphics.DrawPath(borderPen, borderPath);
        if (AccentHeight > 0 && AccentColor != Color.Transparent)
        {
            using var accentPen = new Pen(AccentColor, AccentHeight);
            eventArgs.Graphics.DrawLine(accentPen, Radius, AccentHeight / 2F, Width - Radius, AccentHeight / 2F);
        }
    }
}

internal enum CaptionGlyph { Minimize, Maximize, Close }

internal sealed class WindowCaptionButton : Control
{
    private readonly CaptionGlyph _glyph;
    private bool _hover;
    internal bool ShowsRestoreGlyph => _glyph == CaptionGlyph.Maximize && FindForm()?.WindowState == FormWindowState.Maximized;

    public WindowCaptionButton(CaptionGlyph glyph)
    {
        _glyph = glyph;
        Size = new Size(56, 56);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = glyph switch { CaptionGlyph.Minimize => "最小化", CaptionGlyph.Maximize => "最大化", _ => "关闭" };
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    public void RefreshWindowState()
    {
        if (_glyph == CaptionGlyph.Maximize)
            AccessibleName = FindForm()?.WindowState == FormWindowState.Maximized ? "还原窗口" : "最大化";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var buttonBack = _hover ? _glyph == CaptionGlyph.Close ? Theme.Danger : ColorTranslator.FromHtml("#294766") : Theme.Navy;
        e.Graphics.Clear(buttonBack);
        var cx = ClientSize.Width / 2F;
        var cy = ClientSize.Height / 2F;
        using var pen = new Pen(Color.White, 1.35F) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        if (_glyph == CaptionGlyph.Minimize) e.Graphics.DrawLine(pen, cx - 8, cy, cx + 8, cy);
        else if (_glyph == CaptionGlyph.Maximize)
        {
            var restore = ShowsRestoreGlyph;
            AccessibleName = restore ? "还原窗口" : "最大化";
            if (!restore) e.Graphics.DrawRectangle(pen, cx - 7.5F, cy - 7.5F, 15, 15);
            else
            {
                // Draw only the visible outline segments. Covering an antialiased back
                // rectangle with a filled front rectangle made its top and bottom strokes
                // appear different at 125%/150% DPI.
                var scale = DeviceDpi / 96F;
                var iconCx = MathF.Round(cx);
                var iconCy = MathF.Round(cy);
                var four = MathF.Round(4F * scale);
                var eight = MathF.Round(8F * scale);
                var twelve = MathF.Round(12F * scale);
                var backLeft = iconCx - four;
                var backTop = iconCy - eight;
                var backRight = backLeft + twelve;
                var backBottom = backTop + twelve;
                var frontLeft = iconCx - eight;
                var frontTop = iconCy - four;
                var frontRight = frontLeft + twelve;
                var frontBottom = frontTop + twelve;
                e.Graphics.SmoothingMode = SmoothingMode.None;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                using var crispPen = new Pen(Color.White, Math.Max(1F, MathF.Round(scale)))
                {
                    StartCap = LineCap.Square,
                    EndCap = LineCap.Square,
                    LineJoin = LineJoin.Miter
                };
                e.Graphics.DrawLine(crispPen, backLeft, backTop, backRight, backTop);
                e.Graphics.DrawLine(crispPen, backRight, backTop, backRight, backBottom);
                e.Graphics.DrawLine(crispPen, backLeft, backTop, backLeft, frontTop);
                e.Graphics.DrawLine(crispPen, frontRight, backBottom, backRight, backBottom);
                e.Graphics.DrawRectangle(crispPen, frontLeft, frontTop, twelve, twelve);
            }
        }
        else
        {
            e.Graphics.DrawLine(pen, cx - 7, cy - 7, cx + 7, cy + 7);
            e.Graphics.DrawLine(pen, cx + 7, cy - 7, cx - 7, cy + 7);
        }
    }
}

internal sealed class SearchField : RoundedPanel
{
    public SearchField(TextBox textBox)
    {
        Radius = 7;
        BorderColor = Theme.Border;
        BackColor = Color.White;
        Padding = new Padding(50, 12, 12, 8);
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0);
        textBox.Font = Theme.UiFont(14);
        textBox.GotFocus += (_, _) => { BorderColor = Theme.BlueHover; BorderWidth = 2; Invalidate(); };
        textBox.LostFocus += (_, _) => { BorderColor = Theme.Border; BorderWidth = 1; Invalidate(); };
        Click += (_, _) => textBox.Focus();
        Controls.Add(textBox);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Placeholder, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var y = Height / 2F;
        e.Graphics.DrawEllipse(pen, 20, y - 7, 13, 13);
        e.Graphics.DrawLine(pen, 30.5F, y + 4, 37, y + 10.5F);
    }
}

internal sealed class ModernDropDown : RoundedPanel
{
    public List<string> Items { get; } = [];
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
        var textRect = new Rectangle(24, 0, Math.Max(0, Width - 74), Height);
        using var textFont = Theme.UiFont(14);
        TextRenderer.DrawText(e.Graphics, text, textFont, textRect, Theme.FieldText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.FieldText, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = Width - 27F; var y = Height / 2F;
        if (_open) { e.Graphics.DrawLine(pen, x - 5, y + 2.5F, x, y - 2.5F); e.Graphics.DrawLine(pen, x, y - 2.5F, x + 5, y + 2.5F); }
        else { e.Graphics.DrawLine(pen, x - 5, y - 2.5F, x, y + 2.5F); e.Graphics.DrawLine(pen, x, y + 2.5F, x + 5, y - 2.5F); }
    }

    private void ShowOptions()
    {
        if (_open || Items.Count == 0) return;
        Focus();
        _open = true; BorderColor = Theme.BlueHover; BorderWidth = 2; Invalidate();
        EnsurePopup();
        if (_popup is null || _options is null) return;
        _options.Size = new Size(Width, Items.Count * 48 + 16);
        _options.ResetHover();
        _popup.ClientSize = _options.Size;
        var owner = FindForm();
        _popup.Opacity = owner?.Opacity ?? 1D;
        var desired = PointToScreen(new Point(0, Height + 8));
        var working = Screen.FromControl(this).WorkingArea;
        var x = Math.Clamp(desired.X, working.Left, Math.Max(working.Left, working.Right - _popup.Width));
        var y = desired.Y + _popup.Height <= working.Bottom
            ? desired.Y
            : Math.Max(working.Top, PointToScreen(Point.Empty).Y - _popup.Height - 8);
        _popup.Location = new Point(x, y);
        if (owner is not null) _popup.Show(owner); else _popup.Show();
        _popup.Activate();
    }

    private void EnsurePopup()
    {
        if (_popup is { IsDisposed: false }) return;
        _options = new DropDownOptionsPanel(Items, () => SelectedIndex, index => SelectedIndex = index)
        { Size = new Size(Width, Items.Count * 48 + 16) };
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
            using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 10);
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
        private int _hover = -1;
        public event EventHandler? OptionChosen;

        public DropDownOptionsPanel(IReadOnlyList<string> items, Func<int> selected, Action<int> choose)
        {
            _items = items; _selected = selected; _choose = choose;
            BackColor = Color.White; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        public void ResetHover() { _hover = -1; Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var next = Math.Clamp((e.Y - 8) / 48, 0, _items.Count - 1); if (next != _hover) { _hover = next; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); var index = (e.Y - 8) / 48;
            if (index < 0 || index >= _items.Count) return;
            _choose(index); OptionChosen?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Color.White);
            for (var i = 0; i < _items.Count; i++)
            {
                var row = new Rectangle(8, 8 + i * 48, Width - 16, 48);
                if (i == _selected() || i == _hover)
                {
                    using var path = Theme.RoundedPath(row, 6);
                    using var brush = new SolidBrush(i == _selected() ? ColorTranslator.FromHtml("#D9E7FF") : ColorTranslator.FromHtml("#F3F6FA"));
                    e.Graphics.FillPath(brush, path);
                }
                using var itemFont = Theme.UiFont(18);
                TextRenderer.DrawText(e.Graphics, _items[i], itemFont, new Rectangle(row.X + 16, row.Y, row.Width - 58, row.Height), Theme.FieldText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (i == _selected())
                {
                    using var pen = new Pen(Theme.BlueHover, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    var cx = row.Right - 25; var cy = row.Top + 24;
                    e.Graphics.DrawLines(pen, [new PointF(cx - 6, cy), new PointF(cx - 1, cy + 5), new PointF(cx + 7, cy - 5)]);
                }
            }
            using var border = new Pen(Theme.Border);
            using var borderPath = Theme.RoundedPath(new RectangleF(.5F, .5F, Width - 1, Height - 1), 10);
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
