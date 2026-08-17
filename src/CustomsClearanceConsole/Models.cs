using System.Globalization;
using System.Text.Json.Serialization;

namespace CustomsClearanceConsole;

public sealed class DeclarationRecord
{
    public string SourcePath { get; set; } = "";
    public string DeclarationNo { get; set; } = "";
    public string Consignee { get; set; } = "";
    public string ContractNo { get; set; } = "";
    public string ExitCustoms { get; set; } = "";
    public string DestinationCountry { get; set; } = "";
    public Dictionary<string, decimal> Totals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Confidence { get; set; }
    public string Status { get; set; } = "待识别";
    public string Warning { get; set; } = "";
    public bool IsDuplicate { get; set; }
    public bool IsCanonical { get; set; } = true;
    public string ScreenshotPath { get; set; } = "";
    public DateTime ScannedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string DisplayTotal => Totals.Count == 0
        ? "—"
        : string.Join("  /  ", Totals.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value:N2}"));

    [JsonIgnore]
    public string SourceName => Path.GetFileName(SourcePath);
}

public sealed class AppState
{
    public string LastFolder { get; set; } = "";
    public string ScreenshotFolder { get; set; } = "";
    public string BrowserPreference { get; set; } = "Edge";
    public int PageSize { get; set; } = 20;
    public List<DeclarationRecord> Records { get; set; } = [];
}

public sealed record TextToken(string Text, double Left, double Top, double Right, double Bottom, double Confidence = 100)
{
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
}

public sealed class TextPage
{
    public double Width { get; init; }
    public double Height { get; init; }
    public List<TextToken> Tokens { get; init; } = [];
}

public sealed class DocumentText
{
    public List<TextPage> Pages { get; init; } = [];
    public bool UsedOcr { get; init; }
    public List<TextPage> VerificationPages { get; init; } = [];
    public bool SecondaryOcrAttempted { get; init; }
    public string SecondaryOcrError { get; init; } = "";
}

internal static class CurrencyNames
{
    public static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["美元"] = "USD", ["USD"] = "USD", ["US$"] = "USD",
        ["人民币"] = "CNY", ["CNY"] = "CNY", ["RMB"] = "CNY",
        ["欧元"] = "EUR", ["EUR"] = "EUR",
        ["英镑"] = "GBP", ["GBP"] = "GBP",
        ["日元"] = "JPY", ["JPY"] = "JPY",
        ["港币"] = "HKD", ["HKD"] = "HKD",
        ["加拿大元"] = "CAD", ["加元"] = "CAD", ["CAD"] = "CAD",
        ["澳大利亚元"] = "AUD", ["澳元"] = "AUD", ["AUD"] = "AUD",
        ["新加坡元"] = "SGD", ["SGD"] = "SGD"
    };

    public static string? Normalize(string input)
    {
        var compact = input.Replace(" ", "").Trim('(', ')', '（', '）', ':', '：');
        if (Map.TryGetValue(compact, out var code)) return code;
        return Map.FirstOrDefault(x => compact.Contains(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

internal static class Formatters
{
    public static string MoneyTotals(Dictionary<string, decimal> values) => values.Count == 0
        ? "—"
        : string.Join("  /  ", values.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.ToString("N2", CultureInfo.CurrentCulture)}"));

    public static string MoneyTotalsCompact(Dictionary<string, decimal> values) => values.Count == 0
        ? "—"
        : string.Join(" / ", values.OrderBy(x => x.Key).Select(x => $"{x.Key}{x.Value.ToString("N2", CultureInfo.CurrentCulture)}"));
}
