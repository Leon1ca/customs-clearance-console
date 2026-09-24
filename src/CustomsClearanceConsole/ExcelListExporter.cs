using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CustomsClearanceConsole;

/// <summary>
/// Writes a real OOXML .xlsx workbook (three sheets) using only the framework zip
/// and XML stack, so no Office or NuGet dependency is required.
/// </summary>
internal static class ExcelListExporter
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    // cellXfs indices
    private const int StyleText = 0;
    private const int StyleHeader = 1;
    private const int StyleBody = 2;
    private const int StyleInteger = 3;
    private const int StyleMoney = 4;
    private const int StyleQuantity = 5;
    private const int StyleUnitPrice = 6;
    private const int StyleBold = 7;

    public static void Save(string path, IReadOnlyList<DeclarationRecord> records, DateTime exportedAt)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory ?? ".", Path.GetRandomFileName() + ".xlsx");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                Write(stream, records, exportedAt);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Write(Stream stream, IReadOnlyList<DeclarationRecord> records, DateTime exportedAt)
    {
        var sorted = BatchScanner.SortRecords(records).ToList();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Add(archive, "[Content_Types].xml", Serialize(BuildContentTypes()));
        Add(archive, "_rels/.rels", Serialize(BuildRootRels()));
        Add(archive, "xl/workbook.xml", Serialize(BuildWorkbook()));
        Add(archive, "xl/_rels/workbook.xml.rels", Serialize(BuildWorkbookRels()));
        Add(archive, "xl/styles.xml", Serialize(BuildStyles()));
        Add(archive, "xl/worksheets/sheet1.xml", Serialize(BuildListSheet(sorted)));
        Add(archive, "xl/worksheets/sheet2.xml", Serialize(BuildDetailSheet(sorted)));
        Add(archive, "xl/worksheets/sheet3.xml", Serialize(BuildSummarySheet(sorted)));
    }

    private static string Serialize(XDocument document) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + document.ToString(SaveOptions.DisableFormatting);

    private sealed record CellInfo(string Reference, int Column, string? Type, string? Text, string? Value, bool Formula);

    public static IReadOnlyList<string> Validate(string path, IReadOnlyList<DeclarationRecord>? expected = null)
    {
        var issues = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var required in new[] { "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/worksheets/sheet3.xml" })
                if (archive.GetEntry(required) is null) issues.Add($"缺少 OOXML 部件：{required}");
            if (issues.Count > 0) return issues;

            var workbook = Load(archive, "xl/workbook.xml");
            var styles = Load(archive, "xl/styles.xml");
            var sheetNames = workbook.Descendants(Main + "sheet").Select(x => (string?)x.Attribute("name")).ToList();
            foreach (var name in new[] { "关单列表", "关单明细", "币种汇总" })
                if (!sheetNames.Contains(name)) issues.Add($"缺少工作表：{name}");

            var sheet1 = Load(archive, "xl/worksheets/sheet1.xml");
            var sheet2 = Load(archive, "xl/worksheets/sheet2.xml");
            var sheet3 = Load(archive, "xl/worksheets/sheet3.xml");
            foreach (var sheet in new[] { sheet1, sheet2, sheet3 })
                if (sheet.Descendants(Main + "f").Any())
                    issues.Add("工作簿包含公式节点，外来文本可能被当作公式执行。");

            var rows1 = ReadRows(sheet1);
            if (rows1.Count == 0) issues.Add("关单列表为空。");
            else AssertHeader(rows1[0], ["序号", "报关单号", "境外收货人", "合同协议号", "出境关别", "目的国", "关单总货值", "源文件", "识别状态", "需关注", "重复单号", "合计代表", "截图留存", "分项数"], "关单列表", issues);
            var rows2 = ReadRows(sheet2);
            if (rows2.Count == 0) issues.Add("关单明细为空。");
            else AssertHeader(rows2[0], ["记录序号", "报关单号", "源文件", "页码", "项号", "商品名称", "数量", "单位", "单价", "总价", "币制", "复核总价", "复核数量", "复核单价", "复核商品名称", "复核单位", "整项一致", "金额确认", "说明"], "关单明细", issues);
            var rows3 = ReadRows(sheet3);
            if (rows3.Count == 0) issues.Add("币种汇总为空。");
            else AssertHeader(rows3[0], ["币种", "去重前", "去重后（计入）", "重复扣减", "确认口径"], "币种汇总", issues);

            // R4-6: the raw XML keeps the full decimal, but a cell format with fewer decimal
            // places would round it on screen (e.g. 0.0004 shown as 0). Resolve each
            // quantity/unit-price cell's real number format and flag anything it cannot show.
            var numberFormats = ReadNumberFormats(styles);
            var cellXfs = styles.Descendants(Main + "cellXfs").Elements(Main + "xf")
                .Select(x => (int?)x.Attribute("numFmtId") ?? 0)
                .ToList();
            foreach (var cell in sheet2.Descendants(Main + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r") ?? "");
                if (column is not (7 or 9 or 13 or 14) || cell.Attribute("t") is not null) continue;
                var raw = cell.Element(Main + "v")?.Value;
                if (raw is null || !decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) continue;
                var styleIndex = (int?)cell.Attribute("s") ?? 0;
                if (styleIndex < 0 || styleIndex >= cellXfs.Count) continue;
                var displayedDecimals = DisplayedDecimalPlaces(ResolveNumberFormat(cellXfs[styleIndex], numberFormats));
                if (displayedDecimals is null) continue;
                if (value != 0 && decimal.Round(value, Math.Min(displayedDecimals.Value, 28)) != value)
                    issues.Add($"数量/单价单元格 {cell.Attribute("r")?.Value} 的值 {raw} 超出显示格式可保留的小数位，将显示为零或四舍五入。");
            }

            // A declaration number must always be an inline string, never a numeric value.
            foreach (var cell in sheet1.Descendants(Main + "c"))
            {
                if (cell.Attribute("t") is null && cell.Element(Main + "v") is { } value &&
                    value.Value.Length == 18 && value.Value.All(char.IsAsciiDigit))
                    issues.Add($"18 位报关单号被保存为数值：{value.Value}");
            }

            if (expected is not null)
            {
                var sorted = BatchScanner.SortRecords(expected).ToList();
                if (rows1.Count - 1 != sorted.Count) issues.Add($"关单列表行数 {rows1.Count - 1} 与本批 {sorted.Count} 条不一致。");
                var detailRows = rows2.Count - 1;
                var expectedDetails = sorted.Sum(x => x.LineTotals.Count);
                if (detailRows != expectedDetails) issues.Add($"关单明细行数 {detailRows} 与分项 {expectedDetails} 条不一致。");

                // Exact per-cell read-back for every declaration list row.
                for (var i = 0; i < sorted.Count; i++)
                {
                    var record = sorted[i];
                    var row = rows1[i + 1];
                    if (CellText(row.GetValueOrDefault(2)) != record.DeclarationNo) issues.Add($"第 {i + 1} 行报关单号与本批顺序不一致：{CellText(row.GetValueOrDefault(2))}");
                    if (record.HasValidDeclarationNo && row.GetValueOrDefault(2)?.Type != "inlineStr") issues.Add($"报关单号 {record.DeclarationNo} 不是文本单元。");
                    if (CellText(row.GetValueOrDefault(3)) != record.Consignee) issues.Add($"第 {i + 1} 行收货人丢失：{CellText(row.GetValueOrDefault(3))}");
                    if (CellText(row.GetValueOrDefault(4)) != record.ContractNo) issues.Add($"第 {i + 1} 行合同号丢失或转义错误：{CellText(row.GetValueOrDefault(4))}");
                    if (CellText(row.GetValueOrDefault(8)) != record.SourceName) issues.Add($"第 {i + 1} 行源文件丢失。");
                }

                // Leading zeros and formula-like text survive verbatim.
                var leadingZero = sorted.FirstOrDefault(x => x.DeclarationNo.StartsWith('0'));
                if (leadingZero is not null && !rows1.Skip(1).Any(r => CellText(r.GetValueOrDefault(2)) == leadingZero.DeclarationNo))
                    issues.Add($"前导零报关单号未按文本保留：{leadingZero.DeclarationNo}");
                var formulaLike = sorted.Select(x => x.ContractNo).FirstOrDefault(x => x.StartsWith('=') || x.StartsWith('+') || x.StartsWith('-') || x.StartsWith('@'));
                if (formulaLike is not null && !rows1.Skip(1).Any(r => CellText(r.GetValueOrDefault(4)) == formulaLike))
                    issues.Add($"公式型外来文本未按文本保留：{formulaLike}");

                // Currency summary values are present and numeric.
                var summaryText = rows3.Skip(1).SelectMany(r => r.Values).Where(c => c.Type == "inlineStr").Select(c => c.Text).ToList();
                foreach (var currency in sorted.SelectMany(x => x.Totals.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!summaryText.Contains(currency)) issues.Add($"币种汇总缺少 {currency}。");

                // Detail sheet: missing descriptive values stay empty; verification fields survive.
                var expectedByNumber = sorted.GroupBy(x => x.DeclarationNo).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                foreach (var group in rows2.Skip(1).GroupBy(r => CellText(r.GetValueOrDefault(2)) ?? ""))
                {
                    if (!expectedByNumber.TryGetValue(group.Key, out var source)) continue;
                    var sourceLines = source.SelectMany(x => x.LineTotals).OrderBy(x => x.PageNumber).ThenBy(x => x.Sequence).ToList();
                    var detailLines = group.ToList();
                    for (var i = 0; i < Math.Min(sourceLines.Count, detailLines.Count); i++)
                    {
                        var line = sourceLines[i];
                        var row = detailLines[i];
                        if (string.IsNullOrWhiteSpace(line.ProductName) && row.TryGetValue(6, out var product) && !string.IsNullOrEmpty(product.Text))
                            issues.Add($"缺失商品名称被写成空以外的值：{product.Text}");
                        if (line.Quantity is null && row.TryGetValue(7, out var quantity) && quantity.Value is not null)
                            issues.Add("缺失数量被写成数值，未保持为空。");
                        if (line.UnitPrice is null && row.TryGetValue(9, out var unitPrice) && unitPrice.Value is not null)
                            issues.Add("缺失单价被写成数值，未保持为空。");
                        if (line.Quantity is not null && row.TryGetValue(7, out var quantityCell) &&
                            (!decimal.TryParse(quantityCell.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var quantityValue) || quantityValue != line.Quantity.Value))
                            issues.Add($"数量精度丢失：期望 {line.Quantity.Value}，实际 {quantityCell.Value}。");
                        if (line.UnitPrice is not null && row.TryGetValue(9, out var unitPriceCell) &&
                            (!decimal.TryParse(unitPriceCell.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var unitPriceValue) || unitPriceValue != line.UnitPrice.Value))
                            issues.Add($"单价精度丢失：期望 {line.UnitPrice.Value}，实际 {unitPriceCell.Value}。");
                        if (!string.IsNullOrWhiteSpace(line.VerificationUnit) && CellText(row.GetValueOrDefault(16)) != line.VerificationUnit)
                            issues.Add($"复核单位未导出：期望 {line.VerificationUnit}。");
                        // The overall verdict must never call a row consistent when the
                        // amount differs or when the second engine did not verify it.
                        var consistency = CellText(row.GetValueOrDefault(17));
                        if (line.HasValueDifference && consistency != "存在差异")
                            issues.Add($"存在复核差异的分项未被标记：{consistency}。");
                        if (!line.HasValueDifference && !line.IsFullyVerified && consistency != "未完整复核")
                            issues.Add($"未完整复核的分项被写成“{consistency}”，应为“未完整复核”。");
                        if (!line.HasValueDifference && line.IsFullyVerified && consistency != "一致")
                            issues.Add($"完整复核且一致的分项被写成“{consistency}”。");
                        var amountCheck = CellText(row.GetValueOrDefault(18));
                        if (line.HasAmountDifference && amountCheck != "金额不一致")
                            issues.Add($"金额不一致的分项金额确认写成“{amountCheck}”。");
                        if (!line.HasAmountDifference && line.AmountVerification == "金额未复核" && amountCheck != "金额未复核")
                            issues.Add($"未复核金额被写成“{amountCheck}”，应为“金额未复核”。");
                        if (!line.HasAmountDifference && line.AmountVerification == "金额一致" && amountCheck != "金额一致")
                            issues.Add($"复核金额一致的分项金额确认写成“{amountCheck}”。");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            issues.Add($"xlsx 校验读取失败：{ex.Message}");
        }
        return issues;
    }

    private static void AssertHeader(IReadOnlyDictionary<int, CellInfo> row, IReadOnlyList<string> expected, string sheet, List<string> issues)
    {
        for (var i = 0; i < expected.Count; i++)
        {
            var actual = row.TryGetValue(i + 1, out var cell) ? cell.Text : null;
            if (actual != expected[i]) issues.Add($"{sheet} 表头第 {i + 1} 列应为“{expected[i]}”，实际“{actual}”。");
        }
    }

    private static string? CellText(CellInfo? cell) => cell?.Text;

    private static Dictionary<int, string> ReadNumberFormats(XDocument styles) =>
        styles.Descendants(Main + "numFmt")
            .Select(x => (Id: (int?)x.Attribute("numFmtId"), Code: (string?)x.Attribute("formatCode")))
            .Where(x => x.Id is not null && x.Code is not null)
            .ToDictionary(x => x.Id!.Value, x => x.Code!);

    /// <summary>Maps a style's numFmtId to its format code, covering the custom ids and the
    /// standard builtins 0-49 (0 = General in particular). Unknown ids yield null.</summary>
    private static string? ResolveNumberFormat(int numFmtId, IReadOnlyDictionary<int, string> custom)
    {
        if (custom.TryGetValue(numFmtId, out var code)) return code;
        return numFmtId switch
        {
            0 => "General",
            1 => "0",
            2 => "0.00",
            3 => "#,##0",
            4 => "#,##0.00",
            5 => "$#,##0_);($#,##0)",
            6 => "$#,##0_);[Red]($#,##0)",
            7 => "$#,##0.00_);($#,##0.00)",
            8 => "$#,##0.00_);[Red]($#,##0.00)",
            9 => "0%",
            10 => "0.00%",
            11 => "0.00E+00",
            12 => "# ?/?",
            13 => "# ??/??",
            14 => "mm-dd-yy",
            15 => "d-mmm-yy",
            16 => "d-mmm",
            17 => "mmm-yy",
            18 => "h:mm AM/PM",
            19 => "h:mm:ss AM/PM",
            20 => "h:mm",
            21 => "h:mm:ss",
            22 => "m/d/yy h:mm",
            37 => "#,##0 ;(#,##0)",
            38 => "#,##0 ;[Red](#,##0)",
            39 => "#,##0.00;(#,##0.00)",
            40 => "#,##0.00;[Red](#,##0.00)",
            41 => "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
            42 => "_($* #,##0_);_($* (#,##0);_($* \"-\"_);_(@_)",
            43 => "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
            44 => "_($* #,##0.00_);_($* (#,##0.00);_($* \"-\"??_);_(@_)",
            45 => "mm:ss",
            46 => "[h]:mm:ss",
            47 => "mmss.0",
            48 => "##0.0E+0",
            49 => "@",
            _ => null
        };
    }

    /// <summary>Maximum number of decimal places a format code can display, or null when the
    /// code cannot be interpreted (in which case the value is not constrained).</summary>
    private static int? DisplayedDecimalPlaces(string? formatCode)
    {
        if (string.IsNullOrWhiteSpace(formatCode)) return null;
        if (formatCode.Equals("General", StringComparison.OrdinalIgnoreCase)) return 15;
        // Only the first (positive) section governs this comparison; drop quoted literals and
        // escaped characters so only number placeholders remain.
        var section = formatCode.Split(';')[0];
        section = Regex.Replace(section, "\"[^\"]*\"", "");
        section = Regex.Replace(section, @"\\.", "");
        var dot = section.IndexOf('.');
        if (dot < 0) return 0;
        var count = 0;
        for (var i = dot + 1; i < section.Length; i++)
        {
            if (section[i] is '0' or '#') count++;
            else if (section[i] is '%' or 'E' or 'e') break;
        }
        return count;
    }

    private static List<Dictionary<int, CellInfo>> ReadRows(XDocument sheet)
    {
        var rows = new List<Dictionary<int, CellInfo>>();
        foreach (var row in sheet.Descendants(Main + "row"))
        {
            var cells = new Dictionary<int, CellInfo>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? "";
                var info = new CellInfo(
                    reference,
                    ColumnIndex(reference),
                    (string?)cell.Attribute("t"),
                    cell.Element(Main + "is")?.Element(Main + "t")?.Value,
                    cell.Element(Main + "v")?.Value,
                    cell.Element(Main + "f") is not null);
                cells[info.Column] = info;
            }
            rows.Add(cells);
        }
        return rows;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var character in reference)
        {
            if (character < 'A' || character > 'Z') break;
            index = index * 26 + (character - 'A' + 1);
        }
        return index;
    }

    private static XDocument Load(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    // ---- workbook plumbing ----

    private static XDocument BuildContentTypes()
    {
        XElement Override(string part, string type) =>
            new(ContentTypes + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
        return new XDocument(
            new XElement(ContentTypes + "Types",
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                Override("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                Override("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"),
                Override("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                Override("/xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                Override("/xl/worksheets/sheet3.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
    }

    private static XDocument BuildRootRels() => new(
        new XElement(PackageRel + "Relationships",
            new XElement(PackageRel + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "xl/workbook.xml"))));

    private static XDocument BuildWorkbook() => new(
        new XElement(Main + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XElement(Main + "sheets",
                Sheet("关单列表", 1, "rId1"),
                Sheet("关单明细", 2, "rId2"),
                Sheet("币种汇总", 3, "rId3"))));

    private static XElement Sheet(string name, int id, string relId) =>
        new(Main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", id), new XAttribute(Rel + "id", relId));

    private static XDocument BuildWorkbookRels() => new(
        new XElement(PackageRel + "Relationships",
            WorksheetRel("rId1", "worksheets/sheet1.xml"),
            WorksheetRel("rId2", "worksheets/sheet2.xml"),
            WorksheetRel("rId3", "worksheets/sheet3.xml"),
            new XElement(PackageRel + "Relationship",
                new XAttribute("Id", "rId4"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                new XAttribute("Target", "styles.xml"))));

    private static XElement WorksheetRel(string id, string target) =>
        new(PackageRel + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
            new XAttribute("Target", target));

    private static XDocument BuildStyles()
    {
        XElement Font(int size, bool bold, string? color = null) =>
            new(Main + "font",
                new XElement(Main + "sz", new XAttribute("val", size)),
                new XElement(Main + "name", new XAttribute("val", "Microsoft YaHei")),
                bold ? new XElement(Main + "b") : null,
                color is null ? null : new XElement(Main + "color", new XAttribute("rgb", color)));

        XElement Fill(string rgb) => new(Main + "fill",
            new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Main + "fgColor", new XAttribute("rgb", rgb)),
                new XElement(Main + "bgColor", new XAttribute("indexed", "64"))));

        XElement Border() => new(Main + "border",
            new XElement(Main + "left", new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("rgb", "FFDDE2E9"))),
            new XElement(Main + "right", new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("rgb", "FFDDE2E9"))),
            new XElement(Main + "top", new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("rgb", "FFDDE2E9"))),
            new XElement(Main + "bottom", new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("rgb", "FFDDE2E9"))));

        XElement Xf(int fontId, int fillId, int borderId, int numFmtId, string? horizontal, bool wrap, bool verticalTop = false) =>
            new(Main + "xf",
                new XAttribute("numFmtId", numFmtId),
                new XAttribute("fontId", fontId),
                new XAttribute("fillId", fillId),
                new XAttribute("borderId", borderId),
                new XAttribute("applyNumberFormat", numFmtId != 0 ? 1 : 0),
                new XAttribute("applyFont", 1),
                new XAttribute("applyAlignment", 1),
                new XElement(Main + "alignment",
                    new XAttribute("horizontal", horizontal ?? "left"),
                    new XAttribute("vertical", verticalTop ? "top" : "center"),
                    wrap ? new XAttribute("wrapText", "1") : null));

        return new XDocument(
            new XElement(Main + "styleSheet",
                new XElement(Main + "numFmts",
                    NumFmt(164, "#,##0"),
                    NumFmt(165, "#,##0.00"),
                    NumFmt(166, "#,##0.############################"),
                    NumFmt(167, "#,##0.############################")),
                new XElement(Main + "fonts",
                    Font(11, false),
                    Font(11, true),
                    Font(11, false, "FF5B6778")),
                new XElement(Main + "fills",
                    new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none"))),
                    new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125"))),
                    Fill("FFF7F8FA")),
                new XElement(Main + "borders",
                    new XElement(Main + "border"),
                    Border()),
                new XElement(Main + "cellStyleXfs", Xf(0, 0, 0, 0, null, false)),
                new XElement(Main + "cellXfs",
                    Xf(0, 0, 0, 0, "left", false),        // StyleText
                    Xf(1, 2, 1, 0, "center", true),       // StyleHeader
                    Xf(0, 0, 0, 0, "left", true, true),   // StyleBody
                    Xf(0, 0, 0, 164, "right", false),     // StyleInteger
                    Xf(0, 0, 0, 165, "right", false),     // StyleMoney
                    Xf(0, 0, 0, 166, "right", false),     // StyleQuantity
                    Xf(0, 0, 0, 167, "right", false),     // StyleUnitPrice
                    Xf(1, 0, 0, 0, "left", false)),       // StyleBold
                new XElement(Main + "cellStyles", new XElement(Main + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", 0), new XAttribute("builtinId", 0)))));
    }

    private static XElement NumFmt(int id, string code) =>
        new(Main + "numFmt", new XAttribute("numFmtId", id), new XAttribute("formatCode", code));

    // ---- sheet writers ----

    private static XDocument BuildListSheet(IReadOnlyList<DeclarationRecord> records)
    {
        var currencies = records.SelectMany(x => x.Totals.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var headers = new List<string>
        {
            "序号", "报关单号", "境外收货人", "合同协议号", "出境关别", "目的国", "关单总货值",
            "源文件", "识别状态", "需关注", "重复单号", "合计代表", "截图留存", "分项数"
        };
        headers.AddRange(currencies.Select(c => $"货值 {c}"));
        var widths = new List<int> { 6, 22, 34, 22, 14, 12, 30, 30, 16, 10, 10, 12, 12, 8 };
        widths.AddRange(currencies.Select(_ => 16));

        var rows = new List<List<Cell>> { headers.Select(h => Cell.OfText(h, StyleHeader)).ToList() };
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var row = new List<Cell>
            {
                Cell.OfNumber(i + 1, StyleInteger),
                Cell.OfText(record.DeclarationNo),
                Cell.OfText(record.Consignee),
                Cell.OfText(record.ContractNo),
                Cell.OfText(record.ExitCustoms),
                Cell.OfText(record.DestinationCountry),
                Cell.OfText(record.Totals.Count == 0 ? "—" : record.DisplayTotal),
                Cell.OfText(record.SourceName),
                Cell.OfText(record.Status),
                Cell.OfText(record.NeedsAttention ? "是" : "否"),
                Cell.OfText(record.IsDuplicate ? "是" : "否"),
                Cell.OfText(!record.IsDuplicate ? "—" : record.IsCanonical ? "是" : "否"),
                Cell.OfText(record.HasScreenshot ? "已留存" : "—"),
                Cell.OfNumber(record.LineTotals.Count, StyleInteger)
            };
            foreach (var currency in currencies)
                row.Add(record.Totals.TryGetValue(currency, out var amount) ? Cell.OfNumber(amount, StyleMoney) : Cell.Empty);
            rows.Add(row);
        }
        return BuildSheet(rows, widths);
    }

    private static XDocument BuildDetailSheet(IReadOnlyList<DeclarationRecord> records)
    {
        var headers = new[]
        {
            "记录序号", "报关单号", "源文件", "页码", "项号", "商品名称", "数量", "单位", "单价", "总价", "币制",
            "复核总价", "复核数量", "复核单价", "复核商品名称", "复核单位", "整项一致", "金额确认", "说明"
        };
        var widths = new[] { 8, 22, 28, 6, 6, 30, 12, 10, 12, 14, 8, 14, 12, 12, 30, 10, 10, 10, 40 };
        var rows = new List<List<Cell>> { headers.Select(h => Cell.OfText(h, StyleHeader)).ToList() };
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            foreach (var line in record.LineTotals.OrderBy(x => x.PageNumber).ThenBy(x => x.Sequence))
            {
                rows.Add(
                [
                    Cell.OfNumber(i + 1, StyleInteger),
                    Cell.OfText(record.DeclarationNo),
                    Cell.OfText(record.SourceName),
                    Cell.OfNumber(line.PageNumber, StyleInteger),
                    Cell.OfText(line.ItemNo),
                    Cell.OfText(line.ProductName),
                    line.Quantity is null ? Cell.Empty : Cell.OfNumber(line.Quantity.Value, StyleQuantity),
                    Cell.OfText(line.Unit),
                    line.UnitPrice is null ? Cell.Empty : Cell.OfNumber(line.UnitPrice.Value, StyleUnitPrice),
                    Cell.OfNumber(line.Amount, StyleMoney),
                    Cell.OfText(line.Currency),
                    line.VerificationAmount is null ? Cell.Empty : Cell.OfNumber(line.VerificationAmount.Value, StyleMoney),
                    line.VerificationQuantity is null ? Cell.Empty : Cell.OfNumber(line.VerificationQuantity.Value, StyleQuantity),
                    line.VerificationUnitPrice is null ? Cell.Empty : Cell.OfNumber(line.VerificationUnitPrice.Value, StyleUnitPrice),
                    Cell.OfText(line.VerificationProductName),
                    Cell.OfText(line.VerificationUnit),
                    Cell.OfText(line.ItemConsistency),
                    Cell.OfText(line.AmountVerification),
                    Cell.OfText(line.Note)
                ]);
            }
        }
        return BuildSheet(rows, widths);
    }

    private static XDocument BuildSummarySheet(IReadOnlyList<DeclarationRecord> records)
    {
        var gross = BatchScanner.GrossTotals(records);
        var net = BatchScanner.DeduplicatedTotals(records);
        var currencies = gross.Keys.Union(net.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var headers = new[] { "币种", "去重前", "去重后（计入）", "重复扣减", "确认口径" };
        var widths = new[] { 10, 18, 18, 18, 40 };
        var rows = new List<List<Cell>> { headers.Select(h => Cell.OfText(h, StyleHeader)).ToList() };
        foreach (var currency in currencies)
        {
            var before = gross.GetValueOrDefault(currency);
            var after = net.GetValueOrDefault(currency);
            var deduction = after - before;
            rows.Add(
            [
                Cell.OfText(currency),
                Cell.OfNumber(before, StyleMoney),
                Cell.OfNumber(after, StyleMoney),
                deduction == 0 ? Cell.Empty : Cell.OfNumber(deduction, StyleMoney),
                Cell.OfText("仅汇总已确认的可靠分项；重复单号只计合计代表一份，不做汇率换算。")
            ]);
        }
        if (currencies.Count == 0)
            rows.Add([Cell.OfText("—"), Cell.Empty, Cell.Empty, Cell.Empty, Cell.OfText("尚无已确认金额。")]);
        return BuildSheet(rows, widths);
    }

    private readonly record struct Cell(string? Text, decimal? Number, int Style, bool HasValue)
    {
        public static Cell Empty => new(null, null, StyleText, false);
        public static Cell OfText(string? value, int style = StyleBody) => new(value, null, style, !string.IsNullOrEmpty(value));
        public static Cell OfNumber(decimal value, int style) => new(null, value, style, true);
    }

    private static XDocument BuildSheet(IReadOnlyList<IReadOnlyList<Cell>> rows, IReadOnlyList<int> widths)
    {
        var sheetData = new XElement(Main + "sheetData");
        for (var r = 0; r < rows.Count; r++)
        {
            var rowElement = new XElement(Main + "row", new XAttribute("r", r + 1));
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                if (!cell.HasValue) continue;
                var reference = ColumnName(c + 1) + (r + 1);
                if (cell.Number is not null)
                {
                    rowElement.Add(new XElement(Main + "c",
                        new XAttribute("r", reference),
                        new XAttribute("s", cell.Style),
                        new XElement(Main + "v", cell.Number.Value.ToString(CultureInfo.InvariantCulture))));
                }
                else
                {
                    rowElement.Add(new XElement(Main + "c",
                        new XAttribute("r", reference),
                        new XAttribute("s", cell.Style),
                        new XAttribute("t", "inlineStr"),
                        new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), cell.Text))));
                }
            }
            sheetData.Add(rowElement);
        }

        var dimension = rows.Count == 0 ? "A1" : $"A1:{ColumnName(rows.Max(r => r.Count))}{rows.Count}";
        var cols = new XElement(Main + "cols");
        for (var i = 0; i < widths.Count; i++)
            cols.Add(new XElement(Main + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", widths[i]), new XAttribute("customWidth", 1)));

        var freeze = new XElement(Main + "sheetViews",
            new XElement(Main + "sheetView",
                new XAttribute("workbookViewId", "0"),
                new XElement(Main + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen"))));

        return new XDocument(
            new XElement(Main + "worksheet",
                new XElement(Main + "dimension", new XAttribute("ref", dimension)),
                freeze,
                new XElement(Main + "sheetFormatPr", new XAttribute("defaultRowHeight", 15)),
                cols,
                sheetData));
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }
}
