using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CustomsClearanceConsole;

internal enum UiWeight { Regular, Medium, Bold }

/// <summary>
/// Loads the open-source fonts shipped with the portable package (SIL OFL 1.1).
/// Both GDI+ (PrivateFontCollection) and GDI (AddFontResourceEx, FR_PRIVATE) are
/// registered so native controls that do not use compatible text rendering also
/// resolve the families. Missing files never block startup; a system fallback is used.
/// </summary>
internal static class AppFonts
{
    private const uint FrPrivate = 0x10;
    private const string UiFamily = "Noto Sans SC";
    private const string MonoFamily = "JetBrains Mono";

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);

    private static readonly PrivateFontCollection Collection = new();
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    internal static readonly string[] FontFiles =
    [
        "NotoSansSC-Regular.ttf", "NotoSansSC-Medium.ttf", "NotoSansSC-Bold.ttf",
        "NotoSansSC-Variable.ttf",
        "JetBrainsMono-Regular.ttf", "JetBrainsMono-Medium.ttf", "JetBrainsMono-Bold.ttf"
    ];

    public static void Initialize(string? fontDirectory = null)
    {
        if (_initialized) return;
        _initialized = true;
        fontDirectory ??= Path.Combine(AppContext.BaseDirectory, "fonts");
        foreach (var file in FontFiles)
        {
            var path = Path.Combine(fontDirectory, file);
            if (!File.Exists(path)) continue;
            try
            {
                Collection.AddFontFile(path);
                AddFontResourceEx(path, FrPrivate, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                AppLog.Write($"字体加载失败：{file} · {ex.Message}");
            }
        }
        foreach (var family in Collection.Families) Families[family.Name] = family;
    }

    public static IReadOnlyList<string> LoadedFamilies => Families.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static bool HasUiFamily => FindFamily(UiFamily) is not null;
    public static bool HasMonoFamily => FindFamily(MonoFamily) is not null;

    public static Font Ui(float sizePx, UiWeight weight = UiWeight.Regular) => weight switch
    {
        UiWeight.Bold => Create([UiFamily], sizePx, FontStyle.Bold),
        UiWeight.Medium => Create([UiFamily + " Medium", UiFamily], sizePx, FontStyle.Regular),
        _ => Create([UiFamily], sizePx, FontStyle.Regular)
    };

    public static Font Mono(float sizePx, bool medium = false) => medium
        ? Create([MonoFamily + " Medium", MonoFamily], sizePx, FontStyle.Regular)
        : Create([MonoFamily], sizePx, FontStyle.Regular);

    private static FontFamily? FindFamily(string name) => Families.TryGetValue(name, out var family) ? family : null;

    private static Font Create(IEnumerable<string> candidates, float sizePx, FontStyle style)
    {
        var sizePt = sizePx * 72F / 96F;
        foreach (var name in candidates)
        {
            var family = FindFamily(name);
            if (family is null) continue;
            // Prefer the requested weight. A variable or single-weight face may not
            // expose it; try the real style (GDI/GDI+ can often synthesize it) and only
            // fall back to Regular within the same family when the style is rejected.
            try { return new Font(family, sizePt, style, GraphicsUnit.Point); }
            catch (ArgumentException) { }
            try { return new Font(family, sizePt, FontStyle.Regular, GraphicsUnit.Point); }
            catch (ArgumentException) { }
        }
        return new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, sizePt, style, GraphicsUnit.Point);
    }
}
