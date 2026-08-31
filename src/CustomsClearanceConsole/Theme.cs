using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal static class Theme
{
    public static readonly Color Navy = ColorTranslator.FromHtml("#102D4F");
    public static readonly Color Blue = ColorTranslator.FromHtml("#315C9D");
    public static readonly Color BlueHover = ColorTranslator.FromHtml("#245A92");
    public static readonly Color Surface = Color.White;
    public static readonly Color Canvas = ColorTranslator.FromHtml("#E9EEF5");
    public static readonly Color Border = ColorTranslator.FromHtml("#C7D0DC");
    public static readonly Color Text = ColorTranslator.FromHtml("#1A1C1E");
    public static readonly Color Muted = ColorTranslator.FromHtml("#5F6368");
    public static readonly Color Danger = ColorTranslator.FromHtml("#BA1A1A");
    public static readonly Color DangerSoft = ColorTranslator.FromHtml("#FFF1F0");
    public static readonly Color Success = ColorTranslator.FromHtml("#146C43");
    public static readonly Color Warning = ColorTranslator.FromHtml("#8A4F00");
    public static readonly Color HeaderSoft = ColorTranslator.FromHtml("#DCE6F2");
    public static readonly Color CommandBar = ColorTranslator.FromHtml("#F4F7FA");
    public static readonly Color StrongValue = ColorTranslator.FromHtml("#10213A");
    public static readonly Color FieldText = ColorTranslator.FromHtml("#263750");
    public static readonly Color Placeholder = ColorTranslator.FromHtml("#718099");
    private static readonly float UiDpi = ReadUiDpi();

    private static float ReadUiDpi()
    {
        try { using var graphics = Graphics.FromHwnd(IntPtr.Zero); return graphics.DpiY; }
        catch { return 96F; }
    }

    // The handoff specifies visible pixels. Converting here prevents Windows DPI
    // scaling from making the prototype typography 25-50% larger than designed.
    public static Font UiFont(float pixels, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", pixels * 72F / UiDpi, style, GraphicsUnit.Point);

    public static Button PrimaryButton(string text) => Button(text, ColorTranslator.FromHtml("#245A92"), Color.White, ColorTranslator.FromHtml("#1A4A7C"), ColorTranslator.FromHtml("#1C4F84"));
    public static Button SecondaryButton(string text) => Button(text, Color.White, ColorTranslator.FromHtml("#315064"), ColorTranslator.FromHtml("#C2C7CF"), ColorTranslator.FromHtml("#F3F6FA"));
    public static Button DangerButton(string text) => Button(text, ColorTranslator.FromHtml("#FFF4F2"), ColorTranslator.FromHtml("#A9231F"), ColorTranslator.FromHtml("#D9938D"), ColorTranslator.FromHtml("#FFE9E6"));
    public static Button DangerSolidButton(string text) => Button(text, Danger, Color.White, Danger, ColorTranslator.FromHtml("#A91414"));
    public static Button TonalButton(string text) => Button(text, ColorTranslator.FromHtml("#E5EFFD"), ColorTranslator.FromHtml("#1766D1"), ColorTranslator.FromHtml("#94B6E2"), ColorTranslator.FromHtml("#DCEAFF"));

    public static Button IconButton(string text, UiIcon icon, bool danger = false, bool primary = false, bool tonal = false)
    {
        var button = primary ? PrimaryButton(text) : danger ? DangerButton(text) : tonal ? TonalButton(text) : SecondaryButton(text);
        button.Image = ButtonIcon(icon, primary);
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.Padding = new Padding(0);
        return button;
    }

    public static Bitmap ButtonIcon(UiIcon icon, bool primary = false) =>
        UiIcons.LoadButton(icon, primary, UiDpi);

    private static Button Button(string text, Color back, Color fore, Color border, Color hover)
    {
        var button = new RoundedButton
        {
            Text = text, BackColor = back, ForeColor = fore, BorderColor = border, HoverBackColor = hover,
            PressedBackColor = Blend(hover, Color.Black, .04F), FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, Font = UiFont(17F, FontStyle.Bold),
            UseVisualStyleBackColor = false, TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public static Color Blend(Color foreground, Color background, float backgroundRatio)
    {
        backgroundRatio = Math.Clamp(backgroundRatio, 0F, 1F);
        var foregroundRatio = 1F - backgroundRatio;
        return Color.FromArgb(
            (int)(foreground.R * foregroundRatio + background.R * backgroundRatio),
            (int)(foreground.G * foregroundRatio + background.G * backgroundRatio),
            (int)(foreground.B * foregroundRatio + background.B * backgroundRatio));
    }

    public static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        var diameter = Math.Max(1F, radius * 2F);
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

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
        textBox.Font = Theme.UiFont(18);
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
        using var textFont = Theme.UiFont(18);
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

internal enum UiIcon { Directory, DeclarationClean, ScreenshotClean, Start, ListClean, Folder }
internal enum MetricIcon { File, Duplicate, Gross, Deduplicated }

internal static class UiIcons
{
    public static Bitmap LoadButton(UiIcon icon, bool primary, float dpi)
    {
        var size = dpi >= 168F ? 48 : dpi >= 120F ? 32 : 24;
        var stem = icon switch
        {
            UiIcon.Directory => "directory-settings",
            UiIcon.DeclarationClean => "declaration-clean",
            UiIcon.ScreenshotClean => "screenshot-clean",
            UiIcon.Start => primary ? "start-recognition-white" : "start-recognition-blue",
            UiIcon.ListClean => "list-clean",
            _ => ""
        };
        if (stem.Length > 0)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "button-icons", $"{stem}-{size}.png");
            if (File.Exists(path))
            {
                using var source = new Bitmap(path);
                return new Bitmap(source);
            }
        }
        var color = primary ? Color.White : icon is UiIcon.DeclarationClean or UiIcon.ScreenshotClean or UiIcon.ListClean
            ? ColorTranslator.FromHtml("#A9231F")
            : ColorTranslator.FromHtml("#1766D1");
        return Create(icon, color, size);
    }

    public static Bitmap Create(UiIcon icon, Color color, int size = 24)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size / 24F, size / 24F);
        using var pen = new Pen(color, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case UiIcon.Directory:
                graphics.DrawLines(pen, new PointF[] { new(3.5F, 7.25F), new(3.5F, 6F), new(4F, 4.7F), new(5F, 4.5F), new(9.55F, 4.5F), new(11.75F, 6.75F), new(19F, 6.75F), new(20.5F, 8.25F), new(20.5F, 18F), new(19F, 19.5F), new(5F, 19.5F), new(3.5F, 18F), new(3.5F, 7.25F) });
                graphics.DrawLine(pen, 7, 11, 17, 11); graphics.DrawLine(pen, 9.25F, 9.65F, 9.25F, 12.35F);
                graphics.DrawLine(pen, 7, 15.5F, 17, 15.5F); graphics.DrawLine(pen, 14.75F, 14.15F, 14.75F, 16.85F);
                break;
            case UiIcon.DeclarationClean:
                graphics.DrawLines(pen, new PointF[] { new(7.25F, 3.5F), new(13.35F, 3.5F), new(17.75F, 7.9F), new(17.75F, 19.5F), new(16.75F, 20.5F), new(7.25F, 20.5F), new(6.25F, 19.5F), new(6.25F, 4.5F), new(7.25F, 3.5F) });
                graphics.DrawLines(pen, new PointF[] { new(13.35F, 3.5F), new(13.35F, 7.9F), new(17.75F, 7.9F) });
                graphics.DrawLine(pen, 9.25F, 12.25F, 14.75F, 17.75F); graphics.DrawLine(pen, 14.75F, 12.25F, 9.25F, 17.75F);
                break;
            case UiIcon.ScreenshotClean:
                graphics.DrawRectangle(pen, 3.5F, 4.25F, 17F, 15.5F);
                graphics.DrawLines(pen, new PointF[] { new(5.25F, 18.1F), new(9.55F, 13.55F), new(12.55F, 16.5F), new(14.9F, 14.05F), new(18.75F, 18.1F) });
                graphics.DrawLine(pen, 15.7F, 7.25F, 18.8F, 10.35F); graphics.DrawLine(pen, 18.8F, 7.25F, 15.7F, 10.35F);
                break;
            case UiIcon.Start:
                graphics.DrawLines(pen, new PointF[] { new(8F, 4F), new(5.75F, 4F), new(4F, 5.75F), new(4F, 8F) });
                graphics.DrawLines(pen, new PointF[] { new(16F, 4F), new(18.25F, 4F), new(20F, 5.75F), new(20F, 8F) });
                graphics.DrawLines(pen, new PointF[] { new(20F, 16F), new(20F, 18.25F), new(18.25F, 20F), new(16F, 20F) });
                graphics.DrawLines(pen, new PointF[] { new(8F, 20F), new(5.75F, 20F), new(4F, 18.25F), new(4F, 16F) });
                graphics.DrawRectangle(pen, 9F, 7F, 8F, 10F); graphics.DrawLine(pen, 7F, 13.25F, 17F, 13.25F);
                break;
            case UiIcon.ListClean:
                foreach (var y in new[] { 6.25F, 11.25F, 16.25F }) { graphics.DrawLine(pen, 4.5F, y, 6F, y); graphics.DrawLine(pen, 9F, y, y == 16.25F ? 13F : 19.5F, y); }
                graphics.DrawLine(pen, 15.25F, 14.5F, 19.5F, 18.75F); graphics.DrawLine(pen, 19.5F, 14.5F, 15.25F, 18.75F);
                break;
            case UiIcon.Folder:
                graphics.DrawLines(pen, new PointF[] { new(3F, 7F), new(10F, 7F), new(12F, 9F), new(21F, 9F), new(21F, 19F), new(3F, 19F), new(3F, 7F) });
                break;
        }
        return bitmap;
    }

    public static Bitmap CreateMetric(MetricIcon icon)
    {
        var bitmap = new Bitmap(42, 42);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (back, fore) = icon switch
        {
            MetricIcon.File => (ColorTranslator.FromHtml("#E8F1FF"), ColorTranslator.FromHtml("#1766D1")),
            MetricIcon.Duplicate => (ColorTranslator.FromHtml("#FFEDEA"), ColorTranslator.FromHtml("#D7372F")),
            MetricIcon.Gross => (ColorTranslator.FromHtml("#F2EAFF"), ColorTranslator.FromHtml("#6E45D6")),
            _ => (ColorTranslator.FromHtml("#E6F7ED"), ColorTranslator.FromHtml("#14965F"))
        };
        using (var background = new SolidBrush(back))
        using (var path = Theme.RoundedPath(new RectangleF(0, 0, 42, 42), 8)) graphics.FillPath(background, path);
        using var pen = new Pen(fore, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case MetricIcon.File:
                graphics.DrawLines(pen, new PointF[] { new(15, 12), new(23, 12), new(27, 16), new(27, 30), new(15, 30), new(15, 12) });
                graphics.DrawLines(pen, new PointF[] { new(23, 12), new(23, 17), new(27, 17) });
                graphics.DrawLine(pen, 18, 21, 24, 21); graphics.DrawLine(pen, 18, 25, 24, 25);
                break;
            case MetricIcon.Duplicate:
                graphics.DrawRectangle(pen, 13, 13, 11, 11); graphics.DrawRectangle(pen, 18, 18, 11, 11);
                break;
            case MetricIcon.Gross:
                graphics.DrawEllipse(pen, 12, 12, 18, 18);
                graphics.DrawLines(pen, new PointF[] { new(17.5F, 16.5F), new(21, 20), new(24.5F, 16.5F) });
                graphics.DrawLine(pen, 21, 20, 21, 27); graphics.DrawLine(pen, 17.5F, 22, 24.5F, 22);
                break;
            case MetricIcon.Deduplicated:
                graphics.DrawEllipse(pen, 12, 12, 18, 18); graphics.DrawLines(pen, new PointF[] { new(16.5F, 21), new(19.5F, 24), new(25.5F, 17) });
                break;
        }
        return bitmap;
    }
}

internal sealed class MetricCard : Panel
{
    private readonly Label _value;
    private readonly ToolTip _toolTip;
    private readonly float _defaultValueFontSize;
    private readonly bool _money;

    public MetricCard(string title, string value, string unit, string hint, MetricIcon icon, float valueFontSize = 28F, bool money = false)
    {
        _defaultValueFontSize = valueFontSize;
        _money = money;
        BackColor = Color.White;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(25, 24, 20, 14), Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(new PictureBox { Image = UiIcons.CreateMetric(icon), SizeMode = PictureBoxSizeMode.CenterImage, Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = title + "图标" }, 0, 0);
        var titleLabel = new Label
        {
            Text = title, ForeColor = Theme.FieldText, Font = Theme.UiFont(17F, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
        };
        heading.Controls.Add(titleLabel, 1, 0);
        var initialEmptyMoney = money && value.Trim() == "—";
        var valueLine = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 7, 0, 0) };
        _value = new Label
        {
            Text = value, ForeColor = Theme.StrongValue, Font = Theme.UiFont(initialEmptyMoney ? 32F : valueFontSize, money && !initialEmptyMoney ? FontStyle.Regular : FontStyle.Bold),
            AutoSize = true, MaximumSize = new Size(330, 92), TextAlign = ContentAlignment.TopLeft, AutoEllipsis = false,
            Margin = new Padding(0), UseCompatibleTextRendering = true
        };
        var unitLabel = new Label { Text = unit, ForeColor = Theme.StrongValue, Font = Theme.UiFont(18F, FontStyle.Bold), AutoSize = true, Margin = new Padding(10, 17, 0, 0) };
        valueLine.Controls.Add(_value); if (!string.IsNullOrWhiteSpace(unit)) valueLine.Controls.Add(unitLabel);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(valueLine, 0, 1);
        _toolTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };
        _toolTip.SetToolTip(_value, value);
        Controls.Add(layout);
    }

    public void Set(string value, string hint)
    {
        _value.Text = value;
        var emptyMoney = _money && value.Trim() == "—";
        var lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).DefaultIfEmpty("").ToArray();
        var longestLine = lines.Max(x => x.Length);
        var valueSize = emptyMoney ? 32F : _money
            ? lines.Length >= 4 ? 12F : lines.Length == 3 ? 13F : longestLine > 22 ? 14F : _defaultValueFontSize
            : _defaultValueFontSize;
        _value.Font = Theme.UiFont(valueSize, _money && !emptyMoney ? FontStyle.Regular : FontStyle.Bold);
        _value.AccessibleDescription = value;
        _toolTip.SetToolTip(_value, value);
        _value.Invalidate();
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
