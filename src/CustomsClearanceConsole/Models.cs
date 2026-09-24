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
    public List<DeclarationLineTotal> LineTotals { get; set; } = [];
    public int Confidence { get; set; }
    public string Status { get; set; } = "待识别";
    public string Warning { get; set; } = "";
    public string DuplicateWarning { get; set; } = "";
    public bool IsDuplicate { get; set; }
    public bool IsCanonical { get; set; } = true;
    public string ScreenshotPath { get; set; } = "";
    public DateTime ScannedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool HasValidDeclarationNo => DeclarationNo.Length == 18 && DeclarationNo.All(char.IsAsciiDigit);
    [JsonIgnore]
    public bool NeedsAttention => Status is "需关注" or "识别失败" || LineTotals.Any(x => !x.IsReliable);
    [JsonIgnore]
    public bool HasScreenshot => !string.IsNullOrWhiteSpace(ScreenshotPath) && File.Exists(ScreenshotPath);
    [JsonIgnore]
    public bool HasLineDetails => LineTotals.Count > 0;
    [JsonIgnore]
    public string DisplayStatus => string.Join(Environment.NewLine,
        new[] { IsDuplicate ? "重复单号" : "", NeedsAttention ? Status == "识别失败" ? "识别失败" : "需关注" : "", !IsDuplicate && !NeedsAttention ? "识别完成" : "" }.Where(x => x.Length > 0));
    [JsonIgnore]
    public string PrimaryStatus => Status == "识别失败" ? "识别失败" : IsDuplicate ? "重复" : NeedsAttention ? "需关注" : "正常";
    [JsonIgnore]
    public string AllWarnings => string.Join("；", new[] { Warning, DuplicateWarning }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

    [JsonIgnore]
    public string DisplayTotal => Totals.Count == 0
        ? "—"
        : string.Join("  /  ", Totals.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value:N2}"));

    [JsonIgnore]
    public string SourceName => Path.GetFileName(SourcePath);
}

public sealed class DeclarationLineTotal
{
    public int Sequence { get; set; }
    public int PageNumber { get; set; }
    public string ItemNo { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal? Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? VerificationAmount { get; set; }
    public string VerificationProductName { get; set; } = "";
    public decimal? VerificationQuantity { get; set; }
    public string VerificationUnit { get; set; } = "";
    public decimal? VerificationUnitPrice { get; set; }
    public bool IsReliable { get; set; } = true;
    public string Note { get; set; } = "";

    [JsonIgnore]
    public string DisplayAmount => $"{Currency} {Amount:N2}";

    [JsonIgnore]
    public string DisplayVerification => VerificationAmount is null
        ? "—"
        : $"{Currency} {VerificationAmount.Value:N2}";

    [JsonIgnore]
    public string DisplayProduct => string.IsNullOrWhiteSpace(ProductName) ? "未识别" : ProductName;

    // Display and tooltip text must never round a source value: a non-zero quantity
    // such as 0.0004 has to stay readable, never collapse to a bare "0".
    [JsonIgnore]
    public string DisplayQuantity => Quantity is null ? "—" : NumberFormats.Exact(Quantity.Value);

    [JsonIgnore]
    public string ExactQuantity => Quantity is null ? "—" : NumberFormats.Exact(Quantity.Value);

    [JsonIgnore]
    public string DisplayUnit => string.IsNullOrWhiteSpace(Unit) ? "—" : Unit;

    [JsonIgnore]
    public string DisplayQuantityUnit => Quantity is null
        ? "—"
        : string.IsNullOrWhiteSpace(Unit) ? DisplayQuantity : $"{DisplayQuantity} {Unit}";

    [JsonIgnore]
    public string ExactQuantityUnit => Quantity is null
        ? "—"
        : string.IsNullOrWhiteSpace(Unit) ? ExactQuantity : $"{ExactQuantity} {Unit}";

    // No rounding here either: 0.1234567 must not display as 0.123457.
    [JsonIgnore]
    public string DisplayUnitPrice => UnitPrice is null ? "—" : NumberFormats.Exact(UnitPrice.Value);

    [JsonIgnore]
    public string ExactUnitPrice => UnitPrice is null ? "—" : NumberFormats.Exact(UnitPrice.Value);

    [JsonIgnore]
    public bool HasSecondaryDifference =>
        !string.IsNullOrWhiteSpace(VerificationProductName) && !string.Equals(VerificationProductName, ProductName, StringComparison.Ordinal) ||
        VerificationQuantity is not null && VerificationQuantity != Quantity ||
        !string.IsNullOrWhiteSpace(VerificationUnit) && !string.Equals(VerificationUnit, Unit, StringComparison.Ordinal) ||
        VerificationUnitPrice is not null && VerificationUnitPrice != UnitPrice;

    /// <summary>The verification engine produced an amount and it differs from the primary one.</summary>
    [JsonIgnore]
    public bool HasAmountDifference => VerificationAmount is not null && VerificationAmount.Value != Amount;

    /// <summary>Any primary/secondary disagreement, amount included. Feeds the overall verdict.</summary>
    [JsonIgnore]
    public bool HasValueDifference => HasAmountDifference || HasSecondaryDifference;

    /// <summary>
    /// A line is only "fully verified" when the second engine supplied every comparable
    /// field. A single-engine legacy row is never reported as consistent.
    /// </summary>
    [JsonIgnore]
    public bool IsFullyVerified =>
        VerificationAmount is not null &&
        VerificationQuantity is not null &&
        VerificationUnitPrice is not null &&
        !string.IsNullOrWhiteSpace(VerificationProductName) &&
        !string.IsNullOrWhiteSpace(VerificationUnit);

    /// <summary>
    /// Three-state verdict shared by the detail dialog, Excel and Markdown so an amount
    /// conflict can never be exported as "一致" and an unverified row is never "一致".
    /// </summary>
    [JsonIgnore]
    public string ItemConsistency => HasValueDifference ? "存在差异" : IsFullyVerified ? "一致" : "未完整复核";

    /// <summary>Per-amount verdict; distinguishes a real amount conflict from "not verified".</summary>
    [JsonIgnore]
    public string AmountVerification => HasAmountDifference ? "金额不一致" : VerificationAmount is null ? "金额未复核" : "金额一致";
}

public sealed class AppState
{
    public const int CurrentUiSchemaVersion = 6;
    public int UiSchemaVersion { get; set; } = CurrentUiSchemaVersion;
    public string LastFolder { get; set; } = "";
    public string ScreenshotFolder { get; set; } = "";
    public int PageSize { get; set; } = 50;
    public List<DeclarationRecord> Records { get; set; } = [];
}

public sealed record TextToken(string Text, double Left, double Top, double Right, double Bottom, double Confidence = 100)
{
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
}

public sealed class TextPage
{
    public int PageNumber { get; init; } = 1;
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
    public static string MoneyTotalsLines(Dictionary<string, decimal> values) => values.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, values.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.ToString("N2", CultureInfo.CurrentCulture)}"));

}

/// <summary>
/// Lossless decimal text with thousands separators, used by exports so a value is
/// never rounded to a display-oriented number of decimal places.
/// </summary>
internal static class NumberFormats
{
    public static string Exact(decimal value)
    {
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        var separator = text.IndexOf('.');
        var integerPart = separator < 0 ? text : text[..separator];
        var fraction = separator < 0 ? "" : text[(separator + 1)..];
        if (decimal.TryParse(integerPart, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            integerPart = integer.ToString("#,0", CultureInfo.InvariantCulture);
        return fraction.Length == 0 ? integerPart : integerPart + "." + fraction;
    }
}

/// <summary>
/// Decides whether a completed capture may be written back. A queued completion from an
/// invalidated session (batch switch / list clear / exit) is filtered by the session
/// registry, while this guards the value identity: only a saved capture whose declaration
/// number matches the session may update the current batch. A previously saved session
/// keeps its original screenshot on any later failure.
/// </summary>
internal static class BrowserCapturePolicy
{
    public static bool CanBackfill(string sessionDeclarationNo, string resultState, string resultDeclarationNo) =>
        resultState == "saved" &&
        !string.IsNullOrWhiteSpace(sessionDeclarationNo) &&
        string.Equals(sessionDeclarationNo, resultDeclarationNo, StringComparison.Ordinal);
}
