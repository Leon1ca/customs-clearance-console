using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal static class Theme
{
    public static readonly Color Navy = ColorTranslator.FromHtml("#132D43");
    public static readonly Color Blue = ColorTranslator.FromHtml("#254E68");
    public static readonly Color BlueHover = ColorTranslator.FromHtml("#1C4058");
    public static readonly Color Surface = Color.White;
    public static readonly Color Canvas = ColorTranslator.FromHtml("#F4F6F8");
    public static readonly Color Border = ColorTranslator.FromHtml("#DDE4E9");
    public static readonly Color Text = ColorTranslator.FromHtml("#182C3C");
    public static readonly Color Muted = ColorTranslator.FromHtml("#566676");
    public static readonly Color Danger = ColorTranslator.FromHtml("#A53535");
    public static readonly Color DangerSoft = ColorTranslator.FromHtml("#FFF1F0");
    public static readonly Color Success = ColorTranslator.FromHtml("#236348");
    public static readonly Color Warning = ColorTranslator.FromHtml("#815710");
    public static readonly Color HeaderSoft = ColorTranslator.FromHtml("#F4F6F8");
    public static readonly Color CommandBar = ColorTranslator.FromHtml("#F4F7FA");
    public static readonly Color StrongValue = ColorTranslator.FromHtml("#182C3C");
    public static readonly Color FieldText = ColorTranslator.FromHtml("#182C3C");
    public static readonly Color Placeholder = ColorTranslator.FromHtml("#718099");
    private static readonly float UiDpi = ReadUiDpi();

    private static float ReadUiDpi()
    {
        try { using var graphics = Graphics.FromHwnd(IntPtr.Zero); return graphics.DpiY; }
        catch { return 96F; }
    }

    // Design sizes are logical 96-DPI pixels; WinForms scales them with the window.
    public static Font UiFont(float pixels, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", pixels * 72F / 96F, style, GraphicsUnit.Point);

    public static Font MonoFont(float pixels) => new("Consolas", pixels * 72F / 96F, FontStyle.Regular, GraphicsUnit.Point);

    public static Button PrimaryButton(string text) => Button(text, Blue, Color.White, Blue, BlueHover);
    public static Button SecondaryButton(string text) => Button(text, Color.White, ColorTranslator.FromHtml("#315064"), ColorTranslator.FromHtml("#C2C7CF"), ColorTranslator.FromHtml("#F3F6FA"));
    public static Button DangerButton(string text) => Button(text, ColorTranslator.FromHtml("#FFF4F2"), ColorTranslator.FromHtml("#A9231F"), ColorTranslator.FromHtml("#D9938D"), ColorTranslator.FromHtml("#FFE9E6"));
    public static Button DangerSolidButton(string text) => Button(text, Danger, Color.White, Danger, ColorTranslator.FromHtml("#A91414"));
    public static Button TonalButton(string text) => Button(text, HeaderSoft, Blue, Border, ColorTranslator.FromHtml("#E6EEF4"));

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
            Cursor = Cursors.Hand, Font = UiFont(14F, FontStyle.Bold),
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
