namespace CustomsClearanceConsole;

/// <summary>
/// Exact production target identity for the single-window inquiry page. Parsing the Uri
/// and checking scheme + host + route prevents an unrelated page that merely contains the
/// string "singlewindow" (for example https://example.com/?singlewindow) from being
/// accepted as the verification target.
/// </summary>
internal static class TargetUrlPolicy
{
    public const string PrimaryUrl = "https://www.singlewindow.cn/#/publicInquiryDetail?id=pi4";

    public static bool IsSingleWindowInquiry(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("www.singlewindow.cn", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("singlewindow.cn", StringComparison.OrdinalIgnoreCase)) return false;
        var route = uri.Fragment.Length > 0 ? uri.Fragment : uri.PathAndQuery;
        return route.Contains("/publicInquiryDetail", StringComparison.OrdinalIgnoreCase);
    }
}
