using System.Text;

namespace CustomsClearanceConsole;

internal sealed partial class DocumentExtractor
{
    private readonly RapidOcrEngine _rapidOcr = new();
    private static readonly HashSet<string> ImageExtensions =
        new(BatchScanner.SupportedExtensions.Where(x => x != ".pdf"), StringComparer.OrdinalIgnoreCase);

    public async Task<DocumentText> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ExtractPdfAsync(path, cancellationToken);
        if (ImageExtensions.Contains(Path.GetExtension(path)))
        {
            // A multi-page TIFF (a scanner's usual output) is read page by page like a PDF.
            var pages = new List<TextPage>();
            var frames = OcrImages.LoadPages(path);
            try
            {
                for (var i = 0; i < frames.Count; i++)
                    pages.Add(WithPageNumber(await OcrBitmapAsync(frames[i], cancellationToken), i + 1));
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
            var declarationIndexes = SelectDeclarationPageIndexes(pages);
            if (declarationIndexes.Count > 0) pages = declarationIndexes.Select(index => pages[index]).ToList();
            return new DocumentText { UsedOcr = true, Pages = pages };
        }
        throw new NotSupportedException("仅支持 PDF 与 PNG、JPG、BMP、TIF/TIFF、GIF、WEBP 图片。");
    }

    private async Task<DocumentText> ExtractPdfAsync(string path, CancellationToken cancellationToken)
    {
        var pages = new List<TextPage>();
        var rendered = new List<(int Index, string Path)>();
        using var workspace = new TemporaryDirectory(AppPaths.TempRoot);
        var temp = workspace.Path;

        using (var document = new PdfiumNative.PdfDocument(path))
        {
            for (var i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.LoadPage(i);
                try
                {
                    var textPage = ExtractPdfText(page, i + 1);
                    pages.Add(textPage);
                    if (textPage.Tokens.Count < 30)
                        rendered.Add((i, PdfiumNative.RenderPageToPng(page, i, temp)));
                }
                finally { PdfiumNative.FPDF_ClosePage(page); }
            }
        }

        var usedOcr = rendered.Count > 0;
        foreach (var item in rendered)
        {
            using var bitmap = OcrImages.LoadPages(item.Path).Single();
            pages[item.Index] = WithPageNumber(await OcrBitmapAsync(bitmap, cancellationToken), item.Index + 1);
        }
        var declarationIndexes = SelectDeclarationPageIndexes(pages);
        if (declarationIndexes.Count > 0) pages = declarationIndexes.Select(index => pages[index]).ToList();
        return new DocumentText { Pages = pages, UsedOcr = usedOcr };
    }

    private static TextPage ExtractPdfText(IntPtr page, int pageNumber)
    {
        var width = PdfiumNative.FPDF_GetPageWidth(page);
        var height = PdfiumNative.FPDF_GetPageHeight(page);
        var rotation = ((PdfiumNative.FPDFPage_GetRotation(page) % 4) + 4) % 4;
        var rawWidth = rotation is 1 or 3 ? height : width;
        var rawHeight = rotation is 1 or 3 ? width : height;
        var textPage = PdfiumNative.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return new TextPage { PageNumber = pageNumber, Width = width, Height = height };
        try
        {
            var chars = new List<(char Ch, double L, double T, double R, double B)>();
            var count = PdfiumNative.FPDFText_CountChars(textPage);
            for (var i = 0; i < count; i++)
            {
                var unicode = PdfiumNative.FPDFText_GetUnicode(textPage, i);
                if (unicode == 0 || unicode > char.MaxValue) continue;
                var ch = (char)unicode;
                if (!PdfiumNative.FPDFText_GetCharBox(textPage, i, out var left, out var right, out var bottom, out var top)) continue;
                chars.Add(rotation switch
                {
                    1 => (ch, bottom, left, top, right),
                    2 => (ch, rawWidth - right, bottom, rawWidth - left, top),
                    3 => (ch, rawHeight - top, rawWidth - right, rawHeight - bottom, rawWidth - left),
                    _ => (ch, left, rawHeight - top, right, rawHeight - bottom)
                });
            }
            return new TextPage { PageNumber = pageNumber, Width = width, Height = height, Tokens = GroupCharacters(chars) };
        }
        finally { PdfiumNative.FPDFText_ClosePage(textPage); }
    }

    private static List<TextToken> GroupCharacters(List<(char Ch, double L, double T, double R, double B)> chars)
    {
        var tokens = new List<TextToken>();
        var text = new StringBuilder();
        double l = 0, t = 0, r = 0, b = 0;
        var has = false;

        void Flush()
        {
            if (has && text.Length > 0) tokens.Add(new TextToken(text.ToString(), l, t, r, b));
            text.Clear(); has = false;
        }

        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Ch) || c.Ch is '\r' or '\n') { Flush(); continue; }
            var h = Math.Max(1, Math.Max(b - t, c.B - c.T));
            var sameLine = has && Math.Abs(((t + b) / 2) - ((c.T + c.B) / 2)) < Math.Max(2.5, h * .45);
            var gap = has ? c.L - r : 0;
            var join = sameLine && gap < Math.Max(4.2, h * .7) && gap > -Math.Max(5, h * .5);
            if (has && !join) Flush();
            if (!has) { l = c.L; t = c.T; r = c.R; b = c.B; has = true; }
            else { l = Math.Min(l, c.L); t = Math.Min(t, c.T); r = Math.Max(r, c.R); b = Math.Max(b, c.B); }
            text.Append(c.Ch);
        }
        Flush();
        return tokens;
    }

    /// <summary>
    /// One PP-OCRv5 pass per page. The page is scaled to a 2800px long edge (the detector's
    /// working size). Orientation needs no separate engine: a page lying on its side yields
    /// mostly tall text boxes, and an upside-down page mostly lines the angle classifier turns
    /// by 180 degrees; only then is the page rotated and read again, keeping the reading that
    /// looks most like a declaration.
    /// </summary>
    private async Task<TextPage> OcrBitmapAsync(Bitmap source, CancellationToken cancellationToken)
    {
        var first = await RecognizeRotatedAsync(source, 0, cancellationToken);
        var candidates = new List<RapidOcrPage> { first };
        if (first.TallFraction > .5)
        {
            // On its side: turn it upright one way; if the text then reads upside down, the
            // other way is the right one.
            var quarter = await RecognizeRotatedAsync(source, 90, cancellationToken);
            candidates.Add(quarter);
            if (quarter.FlippedFraction > .5) candidates.Add(await RecognizeRotatedAsync(source, 270, cancellationToken));
        }
        else if (first.FlippedFraction > .5)
        {
            candidates.Add(await RecognizeRotatedAsync(source, 180, cancellationToken));
        }
        // The line-angle classifier reads upside-down lines correctly, so an upside-down page
        // still looks like a declaration by its words; only its geometry is mirrored. Prefer
        // the reading with the fewest upside-down lines.
        var best = candidates
            .OrderBy(x => x.TallFraction > .5 ? 1 : 0)
            .ThenBy(x => x.FlippedFraction > .5 ? 1 : 0)
            .ThenByDescending(x => DeclarationPageScore(x.Page))
            .ThenByDescending(x => x.Page.Tokens.Sum(t => t.Text.Length * t.Confidence))
            .First();
        AppLog.Write($"OCR 页面方向：{string.Join("；", candidates.Select(x => $"{x.Rotation}° 竖框 {x.TallFraction:P0} 倒置 {x.FlippedFraction:P0} 得分 {DeclarationPageScore(x.Page)}"))}；采用 {best.Rotation}°。");
        return best.Page;
    }

    private async Task<RapidOcrPage> RecognizeRotatedAsync(Bitmap source, int rotation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = OcrImages.Prepare(source, rotation);
        var png = OcrImages.ToPng(prepared);
        var page = await _rapidOcr.RecognizeAsync(png, prepared.Width, prepared.Height, cancellationToken);
        return page with { Rotation = rotation };
    }

    private static TextPage WithPageNumber(TextPage page, int pageNumber) => new()
    {
        PageNumber = pageNumber,
        Width = page.Width,
        Height = page.Height,
        Tokens = page.Tokens
    };

    private static List<int> SelectDeclarationPageIndexes(IReadOnlyList<TextPage> pages)
    {
        var selected = new List<int>();
        for (var i = 0; i < pages.Count; i++)
            if (DeclarationPageScore(pages[i]) >= 8) selected.Add(i);
        return selected;
    }

    private static int DeclarationPageScore(TextPage page)
    {
        var compact = string.Concat(page.Tokens.OrderBy(x => x.CenterY).ThenBy(x => x.Left).Select(x => x.Text))
            .Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var score = 0;
        if (compact.Contains("中华人民共和国海关出口货物报关单") || compact.Contains("出口货物报关单")) score += 8;
        if (compact.Contains("海关编号")) score += 4;
        if (compact.Contains("境外收货人")) score += 3;
        if (compact.Contains("项号") && (compact.Contains("总价") || compact.Contains("币制"))) score += 4;
        if (DeclarationNumberPattern().IsMatch(compact)) score += 3;
        if (compact.Contains("页码/页数") || compact.Contains("页码页数")) score += 2;
        if (compact.Contains("通关无纸化出口放行通知书")) score -= 14;
        if (compact.Contains("装货单") || compact.Contains("shippingorder")) score -= 14;
        if (compact.Contains("委托报关协议")) score -= 14;
        return score;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\d)\d{18}(?!\d)")]
    private static partial System.Text.RegularExpressions.Regex DeclarationNumberPattern();
}
