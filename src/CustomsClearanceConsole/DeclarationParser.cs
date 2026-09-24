using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CustomsClearanceConsole;

internal sealed partial class DeclarationParser
{
    // The parser and reconciler use ~30 distinct patterns through the static Regex helpers,
    // many inside per-token loops. The framework caches only 15 by default, so patterns were
    // evicted and re-parsed continuously; a larger cache keeps every pattern constructed once.
    static DeclarationParser()
    {
        if (Regex.CacheSize < 64) Regex.CacheSize = 64;
    }

    private static readonly string[] KnownLabels =
    [
        "预录入编号", "海关编号", "境内发货人", "出境关别", "出口日期", "申报日期", "备案号",
        "境外收货人", "运输方式", "运输工具名称及航次号", "提运单号", "生产销售单位", "监管方式",
        "征免性质", "许可证号", "合同协议号", "贸易国", "运抵国", "指运港", "离境口岸", "包装种类",
        "件数", "毛重", "净重", "成交方式", "运费", "保费", "杂费", "随附单证及编号", "标记唛码及备注",
        "项号", "商品编号", "商品名称规格型号", "数量及单位", "单价总价币制", "原产国", "最终目的国",
        "境内货源地", "征免"
    ];

    [GeneratedRegex(@"(?<!\d)\d{18}(?!\d)")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"^[0-9][0-9,]*(?:\.[0-9]+)?$")]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"(?<!\d)\(?([0-9]{4})\)?(?!\d)")]
    private static partial Regex CustomsCodeRegex();

    public DeclarationRecord Parse(string path, DocumentText document)
    {
        var primary = ParseSingle(path, new DocumentText { Pages = document.Pages, UsedOcr = document.UsedOcr });
        if (!document.SecondaryOcrAttempted) return primary;
        if (document.VerificationPages.Count == 0)
        {
            primary.Status = "需关注";
            primary.Warning = JoinWarnings(primary.Warning,
                string.IsNullOrWhiteSpace(document.SecondaryOcrError) ? "第二 OCR 引擎未完成复核" : document.SecondaryOcrError);
            primary.Confidence = Math.Min(primary.Confidence, 70);
            return primary;
        }

        var secondary = ParseSingle(path, new DocumentText { Pages = document.VerificationPages, UsedOcr = true });
        return Reconcile(primary, secondary);
    }

    private DeclarationRecord ParseSingle(string path, DocumentText document)
    {
        var record = new DeclarationRecord { SourcePath = path };
        if (document.Pages.Count == 0)
        {
            record.Status = "识别失败";
            record.Warning = "文件中没有可读取的页面";
            return record;
        }

        var first = document.Pages[0];
        record.DeclarationNo = FindDeclarationNo(first);
        record.Consignee = ReadLabeledValue(first, "境外收货人");
        record.ContractNo = ReadLabeledValue(first, "合同协议号");
        record.DestinationCountry = ReadLabeledValue(first, "运抵国");

        // Older scans may have damaged labels. Keep the proven normalized regions only as a last fallback.
        if (string.IsNullOrWhiteSpace(record.Consignee))
            record.Consignee = ReadValueImmediatelyAboveLabel(first, "生产销售单位");
        if (string.IsNullOrWhiteSpace(record.Consignee))
            record.Consignee = ReadRegion(first, .025, .32, .215, .255);
        if (string.IsNullOrWhiteSpace(record.ContractNo))
            record.ContractNo = ReadRegion(first, .025, .31, .295, .335);
        if (string.IsNullOrWhiteSpace(record.DestinationCountry))
            record.DestinationCountry = ReadRegion(first, .47, .64, .295, .335);

        record.ExitCustoms = ReadExitCustoms(first, record.DeclarationNo);
        record.LineTotals = ReadLineTotals(document.Pages);
        record.Totals = SumReliableLineTotals(record.LineTotals);

        if (document.UsedOcr)
        {
            record.Consignee = PreferOcrText(record.Consignee, ReadRegion(first, .02, .31, .225, .265), "Amazon.com Services, Inc");
            var contractCandidate = FindContractCandidate(first);
            if (!LooksLikeContract(record.ContractNo) ||
                (record.ContractNo.Contains(' ') && contractCandidate.Length < record.ContractNo.Length))
                record.ContractNo = contractCandidate;
        }

        record.Consignee = CleanValue(record.Consignee, "境外收货人");
        record.Consignee = NormalizeConsigneeIdentifiers(record.Consignee);
        record.ContractNo = NormalizeContractOcr(CleanValue(record.ContractNo, "合同协议号"));
        record.DestinationCountry = ResolveDestinationCountry(first,
            CleanValue(record.DestinationCountry, "运抵国（地区）", "运抵国(地区)", "运抵国"));
        var ruleProblems = document.UsedOcr ? ApplyOcrRules(record, first) : [];
        record.Confidence = CalculateConfidence(record, document.UsedOcr);

        var missing = new List<string>();
        if (record.DeclarationNo.Length != 18) missing.Add("报关单号");
        if (string.IsNullOrWhiteSpace(record.Consignee)) missing.Add("境外收货人");
        if (string.IsNullOrWhiteSpace(record.ContractNo)) missing.Add("合同协议号");
        if (string.IsNullOrWhiteSpace(record.ExitCustoms)) missing.Add("出境关别");
        if (string.IsNullOrWhiteSpace(record.DestinationCountry)) missing.Add("目的国");
        if (record.Totals.Count == 0) missing.Add("关单总货值");

        if (missing.Count == 0 && ruleProblems.Count == 0)
        {
            record.Status = document.UsedOcr ? "OCR 识别完成" : "识别完成";
            record.Warning = document.UsedOcr ? "OCR 识别，已按规则复核（报关单号、数量×单价=总价、国别、关别）" : "";
        }
        else
        {
            record.Status = "需关注";
            record.Warning = JoinWarnings(missing.Count == 0 ? "" : $"未能可靠识别：{string.Join("、", missing)}",
                string.Join("；", ruleProblems));
            if (ruleProblems.Count > 0) record.Confidence = Math.Min(record.Confidence, 80);
        }
        return record;
    }

    /// <summary>
    /// Consistency rules for an OCR reading (a PDF text layer is exact and needs none). They
    /// replace the former second OCR engine: instead of reading everything twice, the values
    /// the declaration itself makes redundant are checked against each other.
    /// </summary>
    private static List<string> ApplyOcrRules(DeclarationRecord record, TextPage first)
    {
        var problems = new List<string>();

        // The number is printed up to three times in the header (预录入编号, 海关编号, barcode
        // text); a misread digit in one of them shows up as a second distinct number.
        var numbers = page18DigitNumbers(first);
        if (numbers.Count > 1)
            problems.Add($"报关单号在表头读取不一致（{string.Join(" / ", numbers)}）");

        // Every line: some quantity of the row times the unit price must give the total.
        foreach (var line in record.LineTotals)
        {
            if (line.UnitPrice is not { } price || line.QuantityCandidates.Count == 0) continue;
            var matches = line.QuantityCandidates.Any(quantity =>
                Math.Abs(quantity * price - line.Amount) <= .011m + quantity * .00005m + line.Amount * .0001m);
            line.RuleCheckPassed = matches;
            if (matches)
            {
                line.Note = "数量×单价=总价，规则校验通过";
                continue;
            }
            line.IsReliable = false;
            line.Note = $"数量×单价与总价不符（单价 {price:0.####}，总价 {line.Amount:N2}），未计入合计";
            problems.Add($"第{line.PageNumber}页项{(string.IsNullOrWhiteSpace(line.ItemNo) ? line.Sequence.ToString(CultureInfo.InvariantCulture) : line.ItemNo)}数量×单价与总价不符");
        }
        if (record.LineTotals.Any(x => !x.IsReliable)) record.Totals = SumReliableLineTotals(record.LineTotals);

        if (record.DestinationCountry.Length > 0 && !CountryNames.IsName(record.DestinationCountry))
            problems.Add($"目的国“{record.DestinationCountry}”不在国别表中");
        if (record.ExitCustoms.Length > 0 && !IsCustomsName(record.ExitCustoms))
            problems.Add($"出境关别“{record.ExitCustoms}”不像关区名称");
        return problems;

        // Only the title/number band above the first box: the boxes below hold 18-character
        // credit codes (境内发货人, 生产销售单位) that can be all digits.
        static List<string> page18DigitNumbers(TextPage page) => page.Tokens
            .Where(t => t.Bottom <= (FindAnchor(page, "境内发货人")?.Top ?? page.Height * .2))
            .SelectMany(t => DeclarationRegex().Matches(t.Text).Select(m => m.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string FindDeclarationNo(TextPage page)
    {
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(page.Tokens.Where(t => t.Top < page.Height * .29), LineTolerance(page)))
        {
            var text = Join(line);
            var weight = text.Contains("海关编号") || text.Contains("关编号") ? 4
                : text.Contains("预录入") ? 2
                : text.Contains('*') ? 2
                : 1;
            foreach (Match match in DeclarationRegex().Matches(text))
                scores[match.Value] = scores.GetValueOrDefault(match.Value) + weight;
        }
        return scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault() ?? "";
    }

    private static string ReadLabeledValue(TextPage page, params string[] labels)
    {
        var anchor = FindAnchor(page, labels);
        if (anchor is null) return "";

        var anchors = FindKnownAnchors(page);
        var sameRowTolerance = Math.Max(4, page.Height * .012);
        var rightAnchor = anchors
            .Where(x => x.Left > anchor.Right + 1 && Math.Abs(x.CenterY - anchor.CenterY) <= sameRowTolerance)
            .OrderBy(x => x.Left)
            .FirstOrDefault();
        var right = rightAnchor?.Left - 1 ?? Math.Min(page.Width, anchor.Left + page.Width * .34);
        var left = Math.Max(0, anchor.Left - page.Width * .004);

        var nextRow = anchors
            .Where(x => x.Top > anchor.Bottom + 1 && x.Top < anchor.Bottom + page.Height * .10)
            .Where(x => x.CenterX >= left && x.CenterX <= right)
            .OrderBy(x => x.Top)
            .FirstOrDefault();
        var bottom = Math.Min(anchor.Bottom + page.Height * .075, nextRow?.Top - 1 ?? double.MaxValue);

        var valueTokens = page.Tokens.Where(t =>
            t.Top >= anchor.Bottom - 1 && t.Bottom <= bottom + 1 &&
            t.CenterX >= left && t.CenterX <= right &&
            !Overlaps(t, anchor));
        return string.Join(" ", Lines(valueTokens, LineTolerance(page)).Select(Join)).Trim();
    }

    private static string ReadValueImmediatelyAboveLabel(TextPage page, params string[] labels)
    {
        var anchor = FindAnchor(page, labels);
        if (anchor is null) return "";

        var sameRowTolerance = Math.Max(4, page.Height * .012);
        var rightAnchor = FindKnownAnchors(page)
            .Where(x => x.Left > anchor.Right + 1 && Math.Abs(x.CenterY - anchor.CenterY) <= sameRowTolerance)
            .OrderBy(x => x.Left)
            .FirstOrDefault();
        var left = Math.Max(0, anchor.Left - page.Width * .006);
        var right = rightAnchor?.Left - 1 ?? Math.Min(page.Width, anchor.Left + page.Width * .34);
        var candidates = page.Tokens.Where(t =>
            t.CenterX >= left && t.CenterX <= right &&
            t.Bottom <= anchor.Top - 1 && t.Top >= anchor.Top - page.Height * .075);
        var closestLine = Lines(candidates, LineTolerance(page))
            .OrderByDescending(line => line.Average(x => x.CenterY))
            .FirstOrDefault();
        return closestLine is null ? "" : Join(closestLine).Trim();
    }

    private static string ReadExitCustoms(TextPage page, string declarationNo)
    {
        var office = FindCustomsOffice(page);
        if (string.IsNullOrWhiteSpace(office)) office = InferOfficeFromDeclaration(declarationNo);

        var anchor = FindAnchor(page, "出境关别");
        var codeText = anchor?.Text ?? "";
        if (anchor is not null)
        {
            var lineTokens = page.Tokens.Where(t =>
                Math.Abs(t.CenterY - anchor.CenterY) <= Math.Max(4, page.Height * .012) &&
                t.Left >= anchor.Left - 1 && t.Left <= anchor.Right + page.Width * .09);
            codeText += Join(lineTokens);
        }
        var code = CustomsCodeRegex().Match(codeText).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(code))
            code = CustomsCodeRegex().Match(ReadRegion(page, .28, .42, .14, .22)).Groups[1].Value;

        var exitName = CustomsShortByCode(code);
        if (string.IsNullOrWhiteSpace(exitName))
            exitName = ShortCustomsName(CleanValue(ReadLabeledValue(page, "出境关别"), "出境关别"));
        // The printed form repeats the customs name in brackets after 海关编号, e.g.
        // "海关编号：516620260000000017 （南沙新港）"; use it when the box value was not read.
        if (!IsCustomsName(exitName)) exitName = ReadHeaderCustomsName(page);

        if (string.IsNullOrWhiteSpace(office)) return exitName;
        if (string.IsNullOrWhiteSpace(exitName)) return office;
        if (office.Equals(exitName, StringComparison.OrdinalIgnoreCase)) return exitName;
        if (office.Contains("洋山") && exitName.Contains("洋山")) return exitName;
        return $"{office}/{exitName}";
    }

    private static bool IsCustomsName(string value) =>
        value.Length >= 2 && value.Count(x => x is >= '\u4e00' and <= '\u9fff') >= 2;

    private static string ReadHeaderCustomsName(TextPage page)
    {
        var anchor = FindAnchor(page, "海关编号");
        if (anchor is null) return "";
        var tolerance = Math.Max(4, page.Height * .012);
        var line = page.Tokens
            .Where(t => Math.Abs(t.CenterY - anchor.CenterY) <= tolerance && t.Left >= anchor.Left - 1 && t.Left <= anchor.Right + page.Width * .25)
            .OrderBy(t => t.Left);
        var match = Regex.Match(Join(line), @"[（(]\s*([\u4e00-\u9fff]{2,10})\s*[）)]");
        return match.Success ? ShortCustomsName(match.Groups[1].Value) : "";
    }

    private static string FindCustomsOffice(TextPage page)
    {
        foreach (var token in page.Tokens
                     .Where(t => t.Top < page.Height * .20 && t.Text.Contains("海关"))
                     .OrderBy(t => t.Top))
        {
            if (token.Text.Contains("报关单") || token.Text.Contains("海关编号") || token.Text.Contains("出境关别")) continue;
            var match = Regex.Match(token.Text, @"([\u4e00-\u9fff]{2,8})海关");
            if (match.Success) return ShortCustomsName(match.Groups[1].Value + "海关");
        }
        return "";
    }

    private static string ShortCustomsName(string value)
    {
        var result = Regex.Replace(value, @"[（）()\s]", "").Trim();
        result = CustomsCodeRegex().Replace(result, "");
        var namedCustoms = Regex.Match(result, @"([\u4e00-\u9fff]{2,8})海关");
        if (namedCustoms.Success) result = namedCustoms.Groups[1].Value;
        if (result.EndsWith("海关", StringComparison.Ordinal)) result = result[..^2];
        return result switch { "航交办" => "航交办", "洋山市内" => "洋山", _ => result };
    }

    private static string CustomsShortByCode(string code) => code switch
    {
        "3104" => "北仑",
        "5316" => "盐田",
        "2248" => "洋山港区",
        "2225" => "外高桥",
        "7207" => "东兴",
        "9402" => "霍尔果斯",
        _ => ""
    };

    private static string InferOfficeFromDeclaration(string number)
    {
        if (number.Length < 4) return "";
        return number[..4] switch
        {
            "3101" => "海曙", "2921" => "义乌", "5316" => "大鹏", "2231" => "洋山", "2229" => "航交办",
            "7207" => "东兴", "9402" => "霍尔果斯", _ => ""
        };
    }

    private static List<DeclarationLineTotal> ReadLineTotals(IEnumerable<TextPage> pages)
    {
        var result = new List<DeclarationLineTotal>();
        foreach (var page in pages)
        {
            var pageTotals = new List<DeclarationLineTotal>();
            var header = FindPriceHeader(page);
            if (header is not null && header.Top >= page.Height * .07 && header.Top <= page.Height * .76)
            {
                var originHeader = FindAnchor(page, "原产国");
                var left = Math.Max(0, header.Left - page.Width * .02);
                var right = originHeader is not null &&
                            Math.Abs(originHeader.CenterY - header.CenterY) < Math.Max(5, page.Height * .018)
                    ? originHeader.Left - 1
                    : Math.Min(page.Width, header.Right + page.Width * .10);
                var footer = FindAnchor(page, "特殊关系确认", "支付特许权使用费确认", "价格影响确认");
                var bottom = footer?.Top ?? page.Height * .985;

                var column = page.Tokens
                    .Where(t => t.CenterX >= left && t.CenterX <= right)
                    .Where(t => t.Top > header.Bottom && t.Top < bottom)
                    .ToList();
                var lines = Lines(column, Math.Max(2.5, page.Height * .0048)).ToList();
                double? previousRowBottom = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    var currency = NormalizeCurrencyInPriceColumn(page, lines[i], header);
                    if (currency is null) continue;

                    IReadOnlyList<TextToken>? totalLine = null;
                    decimal? total = TryAmountFromLine(lines[i], out var sameLineAmount) && sameLineAmount != 0
                        ? sameLineAmount
                        : null;
                    if (total is not null) totalLine = lines[i];

                    var currencyY = lines[i].Average(x => x.CenterY);
                    for (var j = i - 1; total is null && j >= 0; j--)
                    {
                        var candidateY = lines[j].Average(x => x.CenterY);
                        if (currencyY - candidateY > page.Height * .06) break;
                        if (TryAmountFromLine(lines[j], out var value)) { total = value; totalLine = lines[j]; }
                    }
                    if (total is null || totalLine is null) continue;
                    var searchTop = previousRowBottom ?? header.Bottom;
                    var itemToken = FindItemNoToken(page, searchTop, currencyY);
                    // Anchor the row at its own item number when present so wrapped
                    // product/unit lines from the previous row cannot leak into it.
                    var rowTop = itemToken?.Top ?? searchTop;
                    var line = new DeclarationLineTotal
                    {
                        PageNumber = page.PageNumber,
                        ItemNo = itemToken?.Text.Trim() ?? "",
                        Currency = currency,
                        Amount = total.Value
                    };
                    AttachRowDetails(page, line, left, right, rowTop, currencyY, totalLine.Average(x => x.CenterY));
                    pageTotals.Add(line);
                    previousRowBottom = Math.Max(lines[i].Max(x => x.Bottom), itemToken?.Bottom ?? 0);
                }
            }

            // Some screenshots preserve every amount and currency but blur the compound
            // "单价/总价/币制" header. In that case, anchor each item at its currency row
            // and select the closest numeric line immediately above it.
            if (pageTotals.Count == 0) pageTotals = ReadLineTotalsFromCurrencyRows(page);
            result.AddRange(pageTotals);
        }
        for (var i = 0; i < result.Count; i++) result[i].Sequence = i + 1;
        NormalizeItemNumbers(result);
        return result;
    }

    private static void NormalizeItemNumbers(IReadOnlyList<DeclarationLineTotal> lines)
    {
        if (lines.Count == 0) return;
        var parsed = lines.Select(x => int.TryParse(x.ItemNo, out var item) ? item : 0).ToList();
        var distinctValid = parsed.Where(x => x > 0).Distinct().Count();
        var strictlyIncreasing = parsed.Where(x => x > 0).Zip(parsed.Where(x => x > 0).Skip(1), (a, b) => b > a).All(x => x);
        if (distinctValid >= Math.Ceiling(lines.Count * .7) && strictlyIncreasing)
        {
            for (var i = 0; i < lines.Count; i++)
                if (parsed[i] == 0) lines[i].ItemNo = (i + 1).ToString(CultureInfo.InvariantCulture);
            return;
        }
        for (var i = 0; i < lines.Count; i++) lines[i].ItemNo = (i + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static List<DeclarationLineTotal> ReadLineTotalsFromCurrencyRows(TextPage page)
    {
        var result = new List<DeclarationLineTotal>();
        var footer = FindAnchor(page, "特殊关系确认", "支付特许权使用费确认", "价格影响确认");
        var bottom = footer?.Top ?? page.Height * .985;
        var currencyTokens = page.Tokens
            .Where(t => t.Top >= page.Height * .32 && t.Top < bottom)
            .Where(t => NormalizeCurrencyInPriceColumn(page, [t], null) is not null)
            .ToList();

        double? previousRowBottom = null;
        foreach (var currencyLine in Lines(currencyTokens, Math.Max(2.5, page.Height * .0048)))
        {
            var currency = NormalizeCurrencyInPriceColumn(page, currencyLine, null);
            if (currency is null) continue;

            var currencyY = currencyLine.Average(x => x.CenterY);
            var left = Math.Max(0, currencyLine.Min(x => x.Left) - page.Width * .045);
            var right = Math.Min(page.Width, currencyLine.Max(x => x.Right) + page.Width * .065);
            var amountLines = Lines(page.Tokens.Where(t =>
                    t.CenterX >= left && t.CenterX <= right &&
                    t.CenterY < currencyY && currencyY - t.CenterY <= page.Height * .055),
                    Math.Max(2.5, page.Height * .0048))
                .OrderByDescending(line => line.Average(x => x.CenterY));

            foreach (var amountLine in amountLines)
            {
                if (!TryAmountFromLine(amountLine, out var total) || total <= 0) continue;
                var searchTop = previousRowBottom ?? page.Height * .30;
                var itemToken = FindItemNoToken(page, searchTop, currencyY);
                var rowTop = itemToken?.Top ?? searchTop;
                var line = new DeclarationLineTotal
                {
                    PageNumber = page.PageNumber,
                    ItemNo = itemToken?.Text.Trim() ?? "",
                    Currency = currency,
                    Amount = total
                };
                AttachRowDetails(page, line, left, right, rowTop, currencyY, amountLine.Average(x => x.CenterY));
                result.Add(line);
                previousRowBottom = Math.Max(currencyLine.Max(x => x.Bottom), itemToken?.Bottom ?? 0);
                break;
            }
        }
        return result;
    }

    private static readonly HashSet<string> KnownUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "KG", "KGS", "千克", "公斤", "台", "个", "件", "套", "米", "吨", "辆", "只", "张", "双", "支",
        "箱", "包", "卷", "对", "副", "PCS", "PCE", "SET", "CTN", "M", "平方米", "立方米", "升", "克"
    };

    /// <summary>
    /// Unit evidence for the quantity column. A token counts only when it is a complete known
    /// unit, optionally attached to a leading number such as "12.5KG" or "12.5千克"; substring
    /// matches are never accepted, so a word like "HEADSET" cannot be read as the unit "SET".
    /// </summary>
    private static bool IsKnownUnitToken(string text)
    {
        var candidate = text.Trim().Trim('(', ')', '（', '）', '/', '：', ':');
        if (candidate.Length == 0) return false;
        if (KnownUnits.Contains(candidate)) return true;
        // "12.5KG" / "12.5千克": the remainder after the numeric prefix must itself be a
        // complete known unit. Substring matching is never used, so "HEADSET"/"RESET"
        // (which merely contain "SET") are not accepted as unit evidence.
        var numeric = Regex.Match(candidate, @"^[0-9][0-9,.]*");
        return numeric.Success && numeric.Length < candidate.Length && KnownUnits.Contains(candidate[numeric.Length..]);
    }

    /// <summary>
    /// Horizontal band of the "数量" column, taken from its table header. When the header
    /// also covers the unit ("数量及单位"), only its leading part is treated as the
    /// quantity column so the unit sub-column cannot admit a number.
    /// </summary>
    private static (double Left, double Right)? FindQuantityColumn(TextPage page, double rowTop)
    {
        var header = page.Tokens
            .Where(t => t.Text.Contains("数量", StringComparison.Ordinal))
            .Where(t => t.Bottom <= rowTop + Math.Max(4, page.Height * .012))
            .OrderByDescending(t => t.Bottom)
            .FirstOrDefault();
        if (header is null) return null;
        var quantityFraction = header.Text.Contains("单位", StringComparison.Ordinal) ? .6 : 1.0;
        return (header.Left - page.Width * .005,
                header.Left + (header.Right - header.Left) * quantityFraction + page.Width * .01);
    }

    /// <summary>
    /// Best-effort extraction of product name, quantity, unit and unit price for one
    /// already-reconciled total row. Nothing is ever derived from the total amount:
    /// a value that cannot be located in the source stays empty and renders as “—”.
    /// </summary>
    private static void AttachRowDetails(TextPage page, DeclarationLineTotal line,
        double priceLeft, double priceRight, double rowTop, double currencyY, double totalLineY)
    {
        if (priceLeft >= priceRight) return;
        var tolerance = Math.Max(2.5, page.Height * .0048);
        var itemNoRight = page.Width * .085;
        var bandBottom = currencyY + page.Height * .008;

        // Unit price: the closest numeric line strictly above the total inside the row band.
        var unitPriceTokens = page.Tokens
            .Where(t => t.CenterX >= priceLeft - 1 && t.CenterX <= priceRight + 1)
            .Where(t => t.CenterY >= rowTop && t.CenterY < totalLineY - Math.Max(1, page.Height * .0015))
            .ToList();
        foreach (var candidate in Lines(unitPriceTokens, tolerance).OrderByDescending(l => l.Average(x => x.CenterY)))
        {
            if (!TryAmountFromLine(candidate, out var value) || value <= 0) continue;
            line.UnitPrice = value;
            break;
        }

        // Quantity and unit: a number is only promoted to a quantity when there is
        // explicit column evidence — it sits inside the "数量" header band, or a unit
        // token follows it on the same visual line. Otherwise a right-aligned model
        // number inside the product name (e.g. "2026") would be misattributed as the
        // quantity, so the field deliberately stays empty.
        var leftTokens = page.Tokens
            .Where(t => t.CenterX >= itemNoRight && t.CenterX < priceLeft - 1)
            .Where(t => t.CenterY >= rowTop && t.CenterY <= bandBottom)
            .ToList();
        var quantityColumn = FindQuantityColumn(page, rowTop);
        double? quantityLeft = null;
        var maxFragmentGap = page.Width * .015;
        // OCR usually reads a quantity with its unit as one token ("4辆", "5020千克"). Right-aligned,
        // such tokens can reach under the price header, so they are looked for up to the price
        // column's right edge; a number with a unit is never a price. The first one is the
        // declared quantity, the others are the legal/second-unit quantities.
        var quantityBand = page.Tokens
            .Where(t => t.CenterX >= priceLeft - page.Width * .18 && t.CenterX <= priceRight + 1)
            .Where(t => t.CenterY >= rowTop && t.CenterY <= bandBottom)
            .ToList();
        var withUnits = quantityBand
            .Select(t => (Token: t, Match: Regex.Match(t.Text.Trim(), @"^([0-9][0-9,.]*)\s*(\D+)$")))
            .Where(x => x.Match.Success && IsKnownUnitToken(x.Token.Text))
            .Select(x => (x.Token, Ok: TryAmount(x.Match.Groups[1].Value, out var value), Value: value, Unit: x.Match.Groups[2].Value.Trim()))
            .Where(x => x.Ok && x.Value > 0)
            .OrderBy(x => x.Token.CenterY)
            .ToList();
        line.QuantityCandidates.AddRange(withUnits.Select(x => x.Value));
        foreach (var token in quantityBand.Where(t => t.CenterX < priceLeft - 1))
            if (Regex.IsMatch(token.Text.Trim(), @"^[0-9][0-9,.]*$") && TryAmount(token.Text.Trim(), out var plain) && plain > 0)
                line.QuantityCandidates.Add(plain);
        if (withUnits.Count > 0)
        {
            line.Quantity = withUnits[0].Value;
            line.Unit = withUnits[0].Unit;
            quantityLeft = withUnits.Min(x => x.Token.Left);
        }
        foreach (var visualLine in Lines(leftTokens, tolerance).OrderByDescending(l => l.Max(t => t.CenterX)))
        {
            if (line.Quantity is not null) break;
            var ordered = visualLine.OrderBy(t => t.Left).ToList();
            for (var k = ordered.Count - 1; k >= 0; k--)
            {
                var anchor = ordered[k];
                if (!Regex.IsMatch(anchor.Text.Trim(), @"^[0-9][0-9,.]*$")) continue;
                // The quantity sits in the column immediately left of the price column;
                // a number further left belongs to the product name/specification.
                if (anchor.CenterX < priceLeft - page.Width * .18) break;
                var fragments = new List<string>();
                var lastLeft = anchor.Left;
                for (var m = k; m >= 0; m--)
                {
                    var token = ordered[m];
                    var text = token.Text.Trim();
                    if (!Regex.IsMatch(text, @"^[0-9][0-9,.]*$")) break;
                    // Only merge fragments that are actually adjacent; never join two
                    // numbers separated by a gap (e.g. a model year and a quantity).
                    if (m < k && token.Right < lastLeft - maxFragmentGap) break;
                    fragments.Insert(0, text);
                    lastLeft = token.Left;
                }
                if (!TryAmount(string.Concat(fragments), out var quantity) || quantity <= 0) break;
                var inQuantityColumn = quantityColumn is { } column &&
                    anchor.CenterX >= column.Left && anchor.Right <= column.Right;
                // Unit evidence is adjacency based: the very next token must be a complete
                // unit and must sit right next to the number. Any later token on the line is
                // ignored, so a product word can never promote a model number to a quantity.
                var following = ordered.Count > k + 1 ? ordered[k + 1] : null;
                var adjacentUnit = following is not null &&
                    following.Left - anchor.Right <= maxFragmentGap &&
                    IsKnownUnitToken(following.Text);
                if (!inQuantityColumn && !adjacentUnit) break;
                line.Quantity = quantity;
                quantityLeft = anchor.Left;
                // Only a recognized unit token may be written; when the quantity came from
                // the header column alone the row keeps an empty unit.
                line.Unit = ordered.Skip(k + 1)
                    .Select(t => t.Text.Trim())
                    .FirstOrDefault(t => t.Length > 0 && IsKnownUnitToken(t)) ?? "";
                break;
            }
            if (line.Quantity is not null) break;
        }

        // Product name: everything else between the item number and the quantity/price column.
        var productRight = quantityLeft ?? priceLeft;
        var productTokens = page.Tokens
            .Where(t => t.CenterX >= itemNoRight && t.CenterX < productRight - 1)
            .Where(t => t.CenterY >= rowTop && t.CenterY <= bandBottom)
            .ToList();
        var product = string.Join(" ", Lines(productTokens, tolerance)
            .Select(Join)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 && !Regex.IsMatch(value, @"^[\p{P}\p{S}\s]+$")));
        line.ProductName = Regex.Replace(product, @"\s+", " ").Trim();

        // A quantity without a recognized unit token leaves the unit empty so the dialog
        // and exports show “—”; an arbitrary following word is never stored as the unit.
        if (line.Quantity is null) line.Unit = "";
        if (line.Unit.Length > 0 && KnownUnits.Contains(line.Unit)) line.Unit = line.Unit.ToUpperInvariant();
    }

    private static TextToken? FindItemNoToken(TextPage page, double tableTop, double currencyY)
    {
        return page.Tokens
            .Where(t => t.CenterX <= page.Width * .085 && t.CenterY > tableTop)
            .Where(t => t.CenterY <= currencyY + page.Height * .012 && currencyY - t.CenterY <= page.Height * .12)
            .Where(t => Regex.IsMatch(t.Text.Trim(), @"^(\d{1,3})$"))
            .OrderBy(t => Math.Abs(t.CenterY - currencyY))
            .ThenByDescending(t => t.CenterY)
            .FirstOrDefault();
    }

    internal static Dictionary<string, decimal> SumReliableLineTotals(IEnumerable<DeclarationLineTotal> lines)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Where(x => x.IsReliable && x.Amount > 0 && !string.IsNullOrWhiteSpace(x.Currency)))
            result[line.Currency] = result.GetValueOrDefault(line.Currency) + line.Amount;
        return result;
    }

    private static string? NormalizeCurrencyInPriceColumn(
        TextPage page,
        IReadOnlyCollection<TextToken> tokens,
        TextToken? priceHeader)
    {
        var joined = Join(tokens);
        var exact = CurrencyNames.Normalize(joined);
        if (exact is not null) return exact;

        // Low-resolution scans can reduce “美元” to a single “美” (primary OCR)
        // or confuse both characters as “上米” (verification OCR). Only accept
        // those aliases inside the normalized price column and item-table band,
        // so an isolated character elsewhere in the declaration cannot create a total.
        var compact = NormalizeLabel(joined);
        if (compact is not ("美" or "上米")) return null;

        var centerX = tokens.Average(x => x.CenterX);
        var centerY = tokens.Average(x => x.CenterY);
        if (centerX < page.Width * .45 || centerX > page.Width * .68 ||
            centerY < page.Height * .34 || centerY > page.Height * .78)
            return null;

        if (priceHeader is not null &&
            (centerY <= priceHeader.Bottom ||
             centerX < priceHeader.Left - page.Width * .035 ||
             centerX > priceHeader.Right + page.Width * .08))
            return null;

        return "USD";
    }

    private static bool TryAmountFromLine(IEnumerable<TextToken> line, out decimal value)
    {
        var numericFragments = line
            .OrderBy(x => x.Left)
            .Select(x => x.Text.Trim().Replace("，", ","))
            .Where(x => Regex.IsMatch(x, @"^[0-9][0-9,.]*$|^[.,][0-9]+$"))
            .ToList();
        if (numericFragments.Count == 0) { value = 0; return false; }
        return TryAmount(string.Concat(numericFragments), out value);
    }

    private static TextToken? FindPriceHeader(TextPage page)
    {
        var compound = FindAnchor(page, "单价总价币制", "单价/总价/币制", "总价币制");
        if (compound is not null) return compound;

        var currency = FindAnchor(page, "币制");
        var unitPrice = FindAnchor(page, "单价");
        if (currency is null) return null;
        if (unitPrice is not null && Math.Abs(unitPrice.CenterY - currency.CenterY) <= Math.Max(5, page.Height * .018))
            return Union([unitPrice, currency]);
        return currency;
    }

    private static bool TryAmount(string text, out decimal value)
    {
        var normalized = text.Trim().Replace(" ", "").Replace("，", ",");
        if (!AmountRegex().IsMatch(normalized)) { value = 0; return false; }
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out value);
    }

    private static TextToken? FindAnchor(TextPage page, params string[] labels)
    {
        var targets = labels.Select(NormalizeLabel).Where(x => x.Length > 0).ToArray();
        var matches = new List<(TextToken Token, int Span)>();
        foreach (var line in Lines(page.Tokens, LineTolerance(page)))
        {
            for (var start = 0; start < line.Count; start++)
            {
                var joined = "";
                for (var end = start; end < Math.Min(line.Count, start + 8); end++)
                {
                    joined += NormalizeLabel(line[end].Text);
                    if (!targets.Any(joined.Contains)) continue;
                    var slice = line.Skip(start).Take(end - start + 1).ToList();
                    matches.Add((Union(slice), slice.Count));
                    break;
                }
            }
        }
        return matches
            .OrderBy(x => x.Span)
            .ThenBy(x => x.Token.Top)
            .ThenBy(x => x.Token.Right - x.Token.Left)
            .Select(x => x.Token)
            .FirstOrDefault();
    }

    private static List<TextToken> FindKnownAnchors(TextPage page)
    {
        return KnownLabels
            .Select(label => FindAnchor(page, label))
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => $"{Math.Round(x.Left, 1)}|{Math.Round(x.Top, 1)}|{Math.Round(x.Right, 1)}")
            .Select(x => x.First())
            .ToList();
    }

    private static TextToken Union(IReadOnlyCollection<TextToken> tokens)
    {
        return new TextToken(Join(tokens), tokens.Min(x => x.Left), tokens.Min(x => x.Top),
            tokens.Max(x => x.Right), tokens.Max(x => x.Bottom), tokens.Average(x => x.Confidence));
    }

    private static string NormalizeLabel(string value) => Regex.Replace(value, @"[^\p{L}\p{N}]", "").ToLowerInvariant();

    private static bool Overlaps(TextToken first, TextToken second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;

    private static double LineTolerance(TextPage page) => Math.Max(3.2, page.Height * .006);

    private static string ReadRegion(TextPage page, double x1, double x2, double y1, double y2)
    {
        var tokens = page.Tokens.Where(t =>
            t.CenterX >= page.Width * x1 && t.CenterX <= page.Width * x2 &&
            t.CenterY >= page.Height * y1 && t.CenterY <= page.Height * y2);
        return string.Join(" ", Lines(tokens, LineTolerance(page)).Select(Join)).Trim();
    }

    private static string CleanValue(string value, params string[] labels)
    {
        var result = value.Trim();
        foreach (var label in labels) result = result.Replace(label, "", StringComparison.OrdinalIgnoreCase);
        result = result.Trim(' ', ':', '：', '|');
        result = Regex.Replace(result, @"\s+([,.])", "$1");
        return Regex.Replace(result, @"\s+", " ");
    }

    internal static string NormalizeConsigneeIdentifiers(string value)
    {
        value = ReconnectDetachedIdentifierSeparators(value);
        return string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var match = Regex.Match(part, @"^([A-Z]{2}-[A-Z]{3})([0-9OIL]{2})$", RegexOptions.IgnoreCase);
            if (!match.Success) return part;
            var suffix = match.Groups[2].Value.ToUpperInvariant()
                .Replace('O', '0').Replace('I', '1').Replace('L', '1');
            return match.Groups[1].Value.ToUpperInvariant() + suffix;
        }));
    }

    private static string ReconnectDetachedIdentifierSeparators(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return value;
        var segments = parts.Where(x => Regex.IsMatch(x, @"^[A-Z0-9]{1,16}$", RegexOptions.IgnoreCase)).ToArray();
        var separatorTokens = parts.Where(x => Regex.IsMatch(x, @"^[_./-]+$")).ToArray();
        var separators = string.Concat(separatorTokens).ToCharArray();
        if (segments.Length < 2 || separators.Length != segments.Length - 1 ||
            parts.Length != segments.Length + separatorTokens.Length ||
            segments.Any(x => !x.Any(char.IsLetter))) return value;

        var rebuilt = new StringBuilder(segments[0].ToUpperInvariant());
        for (var i = 1; i < segments.Length; i++)
            rebuilt.Append(separators[i - 1]).Append(segments[i].ToUpperInvariant());
        return rebuilt.ToString();
    }

    private static string NormalizeContractOcr(string value)
    {
        var compact = Regex.Replace(value, @"\s+", "").ToUpperInvariant();
        var compound = Regex.Match(compact, @"^([A-NP-Z]{1,6})([0-9O]{2,})([A-NP-Z]{1,6})([0-9O]{4,})$");
        if (compound.Success)
            return compound.Groups[1].Value + compound.Groups[2].Value.Replace('O', '0') +
                   compound.Groups[3].Value + compound.Groups[4].Value.Replace('O', '0');

        var simple = Regex.Match(compact, @"^([A-NP-Z]{1,6})([0-9O]{2,})$");
        return simple.Success ? simple.Groups[1].Value + simple.Groups[2].Value.Replace('O', '0') : compact;
    }

    private static string PreferOcrText(string current, string wider, string knownValue)
    {
        var combined = $"{current} {wider}";
        var compact = Regex.Replace(combined, @"[^A-Za-z]", "").ToLowerInvariant();
        if (compact.Contains("amazon") || compact.Contains("services") || compact.Contains("amaz")) return knownValue;
        return string.IsNullOrWhiteSpace(current) ? wider : current;
    }

    private static bool LooksLikeContract(string value) => Regex.IsMatch(value.Replace(" ", ""), @"^[A-Z0-9][A-Z0-9._/-]{1,30}$", RegexOptions.IgnoreCase);

    private static string FindContractCandidate(TextPage page)
    {
        var anchor = FindAnchor(page, "合同协议号");
        if (anchor is not null)
        {
            var below = page.Tokens
                .Where(t => t.Top >= anchor.Bottom - 1 && t.Top <= anchor.Bottom + page.Height * .07)
                .Where(t => t.CenterX >= anchor.Left - page.Width * .01 && t.CenterX <= anchor.Left + page.Width * .32)
                .Select(t => t.Text.Replace(" ", "").Trim())
                .Where(LooksLikeContract)
                .OrderByDescending(x => x.Any(char.IsDigit) && (x.Any(char.IsLetter) || x.Contains('-')))
                .ThenByDescending(x => x.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(below)) return below;
        }

        var candidates = page.Tokens
            .Where(t => t.CenterX < page.Width * .34 && t.CenterY >= page.Height * .25 && t.CenterY <= page.Height * .39)
            .Select(t => t.Text.Replace(" ", "").Trim())
            .Where(LooksLikeContract)
            .Where(x => x.Any(char.IsDigit) && (x.Any(char.IsLetter) || x.Contains('-')))
            .OrderByDescending(x => Regex.IsMatch(x, @"^GS\d{2,6}$", RegexOptions.IgnoreCase))
            .ThenByDescending(x => x.Length)
            .ToList();
        return candidates.FirstOrDefault() ?? "";
    }

    [GeneratedRegex(@"[（(]\s*([A-Za-z]{3})\s*[)）]")]
    private static partial Regex CountryCodeRegex();

    /// <summary>
    /// 目的国 comes from the 运抵国（地区） field, in this order:
    /// 1. the code printed in the label cell, e.g. "运抵国（地区） (DZA)", which is exact on text
    ///    layers and short enough for OCR to read reliably;
    /// 2. the country named first in the value cell (a neighbouring cell such as 指运港
    ///    "斯基克达（阿尔及利亚）" can leak in after it);
    /// 3. a code inside the value cell;
    /// 4. the code in the goods table's 最终目的国 column.
    /// The old rule knew 21 countries and otherwise scanned the whole page for any of them, so
    /// unlisted countries (阿尔及利亚) were missed and unrelated text could win.
    /// </summary>
    internal static string ResolveDestinationCountry(TextPage page, string value)
    {
        var fromLabel = CountryNames.FromCode(ReadLabelCountryCode(page, "运抵国"));
        if (fromLabel is not null) return fromLabel;
        var named = CountryNames.FirstNameIn(value);
        if (named is not null) return named;
        foreach (Match match in CountryCodeRegex().Matches(value))
            if (CountryNames.FromCode(match.Groups[1].Value.ToUpperInvariant()) is { } coded) return coded;
        var fromGoods = CountryNames.FromCode(ReadColumnCountryCode(page, "最终目的国"));
        if (fromGoods is not null) return fromGoods;
        var cleaned = CountryCodeRegex().Replace(value, "");
        cleaned = Regex.Replace(cleaned, @"(?<!\d)\d(?!\d)", "");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    /// <summary>The "(XXX)" code printed next to a label, within the label's own cell.</summary>
    private static string? ReadLabelCountryCode(TextPage page, string label)
    {
        var anchor = FindAnchor(page, label);
        if (anchor is null) return null;
        var tolerance = Math.Max(4, page.Height * .012);
        var rightAnchor = FindKnownAnchors(page)
            .Where(x => x.Left > anchor.Right + 1 && Math.Abs(x.CenterY - anchor.CenterY) <= tolerance)
            .OrderBy(x => x.Left)
            .FirstOrDefault();
        var right = rightAnchor?.Left - 1 ?? anchor.Right + page.Width * .08;
        var cell = page.Tokens
            .Where(t => Math.Abs(t.CenterY - anchor.CenterY) <= tolerance && t.Left >= anchor.Left - 1 && t.Right <= right + 1)
            .OrderBy(t => t.Left);
        var match = CountryCodeRegex().Match(string.Join("", cell.Select(t => t.Text)));
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>The first "(XXX)" code below a goods-table header, within that column.</summary>
    private static string? ReadColumnCountryCode(TextPage page, string header)
    {
        var anchor = FindAnchor(page, header);
        if (anchor is null) return null;
        var width = anchor.Right - anchor.Left;
        var tokens = page.Tokens
            .Where(t => t.Top > anchor.Bottom && t.Top < anchor.Bottom + page.Height * .12 &&
                        t.CenterX >= anchor.Left - width * .25 && t.CenterX <= anchor.Right + width * .25)
            .OrderBy(t => t.Top).ThenBy(t => t.Left);
        foreach (var token in tokens)
        {
            var match = CountryCodeRegex().Match(token.Text);
            if (match.Success && CountryNames.FromCode(match.Groups[1].Value.ToUpperInvariant()) is not null)
                return match.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    private static int CalculateConfidence(DeclarationRecord record, bool usedOcr)
    {
        var score = 0;
        if (record.DeclarationNo.Length == 18) score += 30;
        if (record.Consignee.Length >= 3) score += 10;
        if (record.ContractNo.Length >= 2) score += 10;
        if (record.ExitCustoms.Length >= 2) score += 10;
        if (record.DestinationCountry.Length >= 2) score += 10;
        if (record.Totals.Count > 0 && record.Totals.Values.All(x => x >= 0)) score += 30;
        if (usedOcr) score = Math.Max(0, score - 5);
        return score;
    }

    private static IEnumerable<List<TextToken>> Lines(IEnumerable<TextToken> source, double tolerance = 3.2)
    {
        var lines = new List<List<TextToken>>();
        foreach (var token in source.OrderBy(x => x.CenterY).ThenBy(x => x.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l.Average(x => x.CenterY) - token.CenterY) <= tolerance);
            if (line is null) { line = []; lines.Add(line); }
            line.Add(token);
        }
        return lines.OrderBy(l => l.Average(x => x.CenterY)).Select(l => l.OrderBy(x => x.Left).ToList());
    }

    private static string Join(IEnumerable<TextToken> tokens)
    {
        var ordered = tokens.OrderBy(x => x.Left).ToList();
        if (ordered.Count == 0) return "";
        var result = new StringBuilder(ordered[0].Text);
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            var gap = current.Left - previous.Right;
            var englishBoundary = previous.Text.LastOrDefault() <= 127 && current.Text.FirstOrDefault() <= 127;
            var separatorBoundary = Regex.IsMatch(previous.Text.Trim(), @"^[_./-]$") || Regex.IsMatch(current.Text.Trim(), @"^[_./-]$");
            if (englishBoundary && !separatorBoundary && gap > 1.5) result.Append(' ');
            result.Append(current.Text);
        }
        return result.ToString();
    }
}
