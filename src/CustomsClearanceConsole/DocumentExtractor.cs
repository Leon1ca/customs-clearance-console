using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CustomsClearanceConsole;

internal sealed partial class DocumentExtractor
{
    private readonly RapidOcrEngine _rapidOcr = new();
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public async Task<DocumentText> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ExtractPdfAsync(path, cancellationToken);
        if (ImageExtensions.Contains(Path.GetExtension(path)))
        {
            var result = await OcrImageAsync(path, cancellationToken);
            return new DocumentText
            {
                UsedOcr = true,
                Pages = [result.Primary],
                VerificationPages = result.Secondary is null ? [] : [result.Secondary],
                SecondaryOcrAttempted = true,
                SecondaryOcrError = result.SecondaryError
            };
        }
        throw new NotSupportedException("仅支持 PDF、PNG、JPG、BMP、TIF/TIFF 文件。");
    }

    private async Task<DocumentText> ExtractPdfAsync(string path, CancellationToken cancellationToken)
    {
        var pages = new List<TextPage>();
        var rendered = new List<(int Index, string Path)>();
        var temp = Path.Combine(AppPaths.TempRoot, Guid.NewGuid().ToString("N"));

        using (var document = new PdfiumNative.PdfDocument(path))
        {
            for (var i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.LoadPage(i);
                try
                {
                    var textPage = ExtractPdfText(page);
                    pages.Add(textPage);
                    if (textPage.Tokens.Count < 30)
                        rendered.Add((i, PdfiumNative.RenderPageToPng(page, i, temp)));
                }
                finally { PdfiumNative.FPDF_ClosePage(page); }
            }
        }

        var usedOcr = rendered.Count > 0;
        var verificationPages = pages.ToList();
        var secondaryErrors = new List<string>();
        foreach (var item in rendered)
        {
            var result = await OcrImageAsync(item.Path, cancellationToken);
            pages[item.Index] = result.Primary;
            verificationPages[item.Index] = result.Secondary ?? result.Primary;
            if (!string.IsNullOrWhiteSpace(result.SecondaryError)) secondaryErrors.Add(result.SecondaryError);
        }
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        return new DocumentText
        {
            Pages = pages,
            UsedOcr = usedOcr,
            VerificationPages = usedOcr && secondaryErrors.Count == 0 ? verificationPages : [],
            SecondaryOcrAttempted = usedOcr,
            SecondaryOcrError = string.Join("；", secondaryErrors.Distinct())
        };
    }

    private static TextPage ExtractPdfText(IntPtr page)
    {
        var width = PdfiumNative.FPDF_GetPageWidth(page);
        var height = PdfiumNative.FPDF_GetPageHeight(page);
        var rotation = ((PdfiumNative.FPDFPage_GetRotation(page) % 4) + 4) % 4;
        var rawWidth = rotation is 1 or 3 ? height : width;
        var rawHeight = rotation is 1 or 3 ? width : height;
        var textPage = PdfiumNative.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return new TextPage { Width = width, Height = height };
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
            return new TextPage { Width = width, Height = height, Tokens = GroupCharacters(chars) };
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

    private async Task<OcrPageResult> OcrImageAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(AppPaths.TesseractExe))
            throw new FileNotFoundException("该文件需要 OCR，但程序包中的 Tesseract 组件缺失。", AppPaths.TesseractExe);

        using var source = Image.FromFile(path);
        var temp = Path.Combine(AppPaths.TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var ocrInput = path;
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge < 2400 || longEdge > 3000)
        {
            var factor = Math.Clamp(2800d / longEdge, .25, 6d);
            var enlarged = Path.Combine(temp, "input.png");
            using var bitmap = new Bitmap(Math.Max(1, (int)(source.Width * factor)), Math.Max(1, (int)(source.Height * factor)));
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }
            bitmap.Save(enlarged, System.Drawing.Imaging.ImageFormat.Png);
            ocrInput = enlarged;
        }
        var tokens = await RunTesseractPassAsync(ocrInput, temp, 3, cancellationToken);
        var compactOcrText = string.Concat(tokens.OrderBy(t => t.Top).ThenBy(t => t.Left).Select(t => t.Text));
        var looksLikeDeclaration = DeclarationNumberPattern().IsMatch(compactOcrText) ||
                                   compactOcrText.Contains("报关单") || compactOcrText.Contains("币制");
        var hasCurrency = tokens.Any(t => CurrencyNames.Normalize(t.Text) is not null);
        if (looksLikeDeclaration && !hasCurrency)
        {
            var sparseTokens = await RunTesseractPassAsync(ocrInput, temp, 11, cancellationToken);
            foreach (var token in sparseTokens)
            {
                var duplicate = tokens.Any(existing => OverlapRatio(existing, token) >= .55);
                if (!duplicate) tokens.Add(token);
            }
        }

        var resultWidth = source.Width;
        var resultHeight = source.Height;
        if (!ocrInput.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            using var ocrSource = Image.FromFile(ocrInput);
            resultWidth = ocrSource.Width; resultHeight = ocrSource.Height;
        }
        var primary = new TextPage { Width = resultWidth, Height = resultHeight, Tokens = tokens };
        TextPage? secondary = null;
        var secondaryError = "";
        try
        {
            secondary = await _rapidOcr.RecognizeAsync(ocrInput, resultWidth, resultHeight, cancellationToken);
            if (secondary.Tokens.Count == 0) secondaryError = "第二 OCR 引擎未检测到文字";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            secondaryError = $"第二 OCR 引擎不可用：{ex.GetBaseException().Message}";
            AppLog.Write($"{secondaryError}\n文件：{path}\n{ex}");
        }
        try { Directory.Delete(temp, true); } catch { }
        return new OcrPageResult(primary, secondary, secondaryError);
    }

    private sealed record OcrPageResult(TextPage Primary, TextPage? Secondary, string SecondaryError);

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\d)\d{18}(?!\d)")]
    private static partial System.Text.RegularExpressions.Regex DeclarationNumberPattern();

    private static async Task<List<TextToken>> RunTesseractPassAsync(
        string input, string folder, int pageSegmentationMode, CancellationToken cancellationToken)
    {
        var outputBase = Path.Combine(folder, $"ocr-{pageSegmentationMode}");
        var start = new ProcessStartInfo
        {
            FileName = AppPaths.TesseractExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(AppPaths.TesseractExe)!
        };
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(outputBase);
        start.ArgumentList.Add("-l"); start.ArgumentList.Add("chi_sim+eng");
        start.ArgumentList.Add("--oem"); start.ArgumentList.Add("1");
        start.ArgumentList.Add("--psm"); start.ArgumentList.Add(pageSegmentationMode.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--tessdata-dir"); start.ArgumentList.Add(AppPaths.Tessdata);
        start.ArgumentList.Add("tsv");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 OCR 组件。");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        await outputTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"OCR 失败：{error.Trim()}");

        var tokens = new List<TextToken>();
        foreach (var line in await File.ReadAllLinesAsync(outputBase + ".tsv", cancellationToken))
        {
            var parts = line.Split('\t');
            if (parts.Length < 12 || parts[0] == "level" || string.IsNullOrWhiteSpace(parts[11])) continue;
            if (!double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || confidence < 15) continue;
            if (!double.TryParse(parts[6], out var left) || !double.TryParse(parts[7], out var top) ||
                !double.TryParse(parts[8], out var width) || !double.TryParse(parts[9], out var height)) continue;
            tokens.Add(new TextToken(parts[11].Trim(), left, top, left + width, top + height, confidence));
        }
        return tokens;
    }

    private static double OverlapRatio(TextToken first, TextToken second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        var intersection = width * height;
        var firstArea = Math.Max(1, (first.Right - first.Left) * (first.Bottom - first.Top));
        var secondArea = Math.Max(1, (second.Right - second.Left) * (second.Bottom - second.Top));
        return intersection / Math.Min(firstArea, secondArea);
    }
}
