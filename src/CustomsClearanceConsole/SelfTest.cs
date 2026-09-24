using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CustomsClearanceConsole;

internal static class SelfTest
{
    public static void RunUiContracts()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var copied = MainForm.FormatCellSelection([
            (1, 2, "第二行第二项"),
            (0, 4, "只选这一格"),
            (1, 1, "第二行第一项")
        ]);
        var expected = $"只选这一格{Environment.NewLine}第二行第一项\t第二行第二项";
        if (copied != expected) throw new InvalidOperationException($"单元格复制格式不符合约定：{copied}");

        var records = new List<DeclarationRecord>
        {
            new() { DeclarationNo = "292120260001476464", Confidence = 90, Totals = new() { ["USD"] = 100m } },
            new() { DeclarationNo = "292120260001476464", Confidence = 80, Totals = new() { ["USD"] = 100m } },
            new() { DeclarationNo = "310120250516971238", Confidence = 90, Totals = new() { ["CNY"] = 200m } }
        };
        BatchScanner.MarkDuplicates(records);
        if (records.Count(x => x.IsDuplicate) != 2 || BatchScanner.DeduplicatedTotals(records)["USD"] != 100m)
            throw new InvalidOperationException("重复检测或去重合计不符合约定。");

        var rootRejected = false;
        try { FileCleanupService.ValidateTargetFolder(Path.GetPathRoot(Environment.SystemDirectory)!); }
        catch (InvalidOperationException) { rootRejected = true; }
        if (!rootRejected) throw new InvalidOperationException("目录清理安全规则未拦截磁盘根目录。");

        var browser = BrowserValidation.ResolveBrowser();
        if (!File.Exists(browser.Path)) throw new InvalidOperationException("未能解析可用于自动核验的浏览器。");
        if (!BrowserValidation.PrimaryUrl.EndsWith("id=pi4", StringComparison.Ordinal))
            throw new InvalidOperationException("核验页面未指向报关单状态查询 pi4。");

        if (DeclarationParser.NormalizeConsigneeIdentifiers("HNB TX US _ _") != "HNB_TX_US")
            throw new InvalidOperationException("低位下划线未按结构化标识符顺序回接。");
        if (DeclarationParser.NormalizeConsigneeIdentifiers("HNB TX US __") != "HNB_TX_US")
            throw new InvalidOperationException("合并后的低位下划线未按结构化标识符顺序回接。");
        if (DeclarationParser.NormalizeConsigneeIdentifiers("Vietnam Gold Star Investment Company Limited") != "Vietnam Gold Star Investment Company Limited")
            throw new InvalidOperationException("普通境外收货人名称被错误重组。");

        using (var copyMenu = new CopyContextMenu(() => { }))
            if (copyMenu.Items.Count != 1)
                throw new InvalidOperationException("右键复制菜单未保持为单一操作项。");

        foreach (var stem in new[] { Ui2.Play, Ui2.Export, Ui2.FileExcel, Ui2.FileMarkdownInk, Ui2.Camera, Ui2.CameraBlue, Ui2.External, Ui2.TrashInk, Ui2.TrashRed, Ui2.Folder, Ui2.FolderUpload, Ui2.Search, Ui2.Alert, Ui2.CheckGreen, Ui2.Close, Ui2.SettingsWhite, Ui2.SettingsInk, Ui2.Stop, Ui2.ChevronDownNavy, Ui2.AmountEmpty })
            if (!new[] { 16, 24, 32, 48 }.Any(size => UiV2Icons.AssetExists(stem, size)))
                throw new InvalidOperationException($"缺少界面图标资源：{stem}");
        using (var icon24 = UiV2Icons.Load(Ui2.CameraBlue, 16, 96F))
        using (var icon36 = UiV2Icons.Load(Ui2.CameraBlue, 16, 144F))
        {
            if (icon24 is null || icon24.Width != 16) throw new InvalidOperationException("96 DPI 图标未选择 16px。");
            if (icon36 is null || icon36.Width != 24) throw new InvalidOperationException("144 DPI 图标未选择 24px。");
        }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "assets", "ui-v2", "icons", "png", "camera-blue-24.png")))
            throw new InvalidOperationException("UI v2 图标资源未复制到程序目录。");

        VerifyScaleMath();
        RunDetailConsistencyRegression();
        RunDetailMultiCurrencyRegression();
        RunMultiCurrencySummaryRegression();
        RunCopySelectionRegression();
        RunDesignMenuInteractionRegression();
        RunLineDetailParseRegression();
        RunExcelExportRegression();
        RunLineTotalSafetyRegression();
        RunMarkdownExportRegression();
        RunSplitAmountAndFuzzyCurrencyRegression();
        RunDropDownLifecycleRegression();
        RunCaptionStateRegression();
        RunEnterpriseLayoutRegression();
        Console.WriteLine("UI_CONTRACTS_OK");
    }

    private static void VerifyScaleMath()
    {
        if (UiScale.Px(96, 24) != 24 || UiScale.Px(120, 24) != 30 || UiScale.Px(144, 24) != 36 || UiScale.Px(192, 24) != 48)
            throw new InvalidOperationException("DPI 缩放假定 24 逻辑像素未得到 24/30/36/48。");
        var layout = Responsive.Compute(1200, 720);
        if (!layout.Table.PortDestMerged) throw new InvalidOperationException("1200 宽时未合并出境关别/目的国。");
        var wide = Responsive.Compute(1600, 1000);
        if (wide.Table.PortDestMerged) throw new InvalidOperationException("1600 宽时不应合并出境关别/目的国。");
        if (wide.CompactHeight) throw new InvalidOperationException("1000 高不应判定为紧凑高度。");
        if (!Responsive.Compute(1600, 700).CompactHeight) throw new InvalidOperationException("700 高应判定为紧凑高度。");
    }

    private static void RunDetailConsistencyRegression()
    {
        var empty = new DeclarationRecord { DeclarationNo = "310120260000000001", LineTotals = [] };
        var emptyResult = DetailForm.EvaluateConsistency(empty);
        if (emptyResult.Consistent || !emptyResult.Message.Contains("未保存分项"))
            throw new InvalidOperationException("缺少分项时不得显示一致。");

        var reliable = new DeclarationRecord
        {
            DeclarationNo = "310120260000000002",
            LineTotals = [new DeclarationLineTotal { Sequence = 1, PageNumber = 1, ItemNo = "1", Currency = "USD", Amount = 100m, IsReliable = true }]
        };
        reliable.Totals = DeclarationParser.SumReliableLineTotals(reliable.LineTotals);
        var reliableResult = DetailForm.EvaluateConsistency(reliable);
        if (!reliableResult.Consistent || !reliableResult.Message.Contains("可靠分项"))
            throw new InvalidOperationException("可靠分项应显示可靠分项合计口径。");
        if (reliableResult.Message.Contains("一致；") || reliableResult.Message.StartsWith("分项合计与关单总价一致", StringComparison.Ordinal))
            throw new InvalidOperationException("同源合计不得声称独立一致的结论。");

        var conflict = new DeclarationRecord
        {
            DeclarationNo = "310120260000000003",
            LineTotals = [new DeclarationLineTotal { Sequence = 1, PageNumber = 1, ItemNo = "1", Currency = "USD", Amount = 100m, VerificationAmount = 90m, IsReliable = false }]
        };
        conflict.Totals = DeclarationParser.SumReliableLineTotals(conflict.LineTotals);
        var conflictResult = DetailForm.EvaluateConsistency(conflict);
        if (conflictResult.Consistent || !conflictResult.Message.Contains("未计入确认合计"))
            throw new InvalidOperationException("冲突分项不得计入确认合计。");
    }

    /// <summary>R3-3: detail rows carry their own currency and per-currency totals never mix.</summary>
    private static void RunDetailMultiCurrencyRegression()
    {
        var record = new DeclarationRecord
        {
            DeclarationNo = "310120260000000009",
            LineTotals =
            [
                new DeclarationLineTotal { Sequence = 1, PageNumber = 1, ItemNo = "1", ProductName = new string('超', 80) + "长商品名称", Quantity = 10, Unit = "KG", UnitPrice = 1.5m, Currency = "USD", Amount = 15m, IsReliable = true },
                new DeclarationLineTotal { Sequence = 2, PageNumber = 1, ItemNo = "2", ProductName = "混合币种项", Quantity = 5, Unit = "台", UnitPrice = 20m, Currency = "EUR", Amount = 100m, IsReliable = true },
                new DeclarationLineTotal { Sequence = 3, PageNumber = 2, ItemNo = "3", ProductName = "冲突项", Quantity = 1, Unit = "KG", UnitPrice = 9m, Currency = "USD", Amount = 9m, VerificationAmount = 8m, IsReliable = false, Note = "两引擎不一致" }
            ]
        };
        record.Totals = DeclarationParser.SumReliableLineTotals(record.LineTotals);
        var summary = DetailForm.CurrencySummaryText(record);
        if (!summary.Contains("USD 15.00") || !summary.Contains("EUR 100.00"))
            throw new InvalidOperationException("明细未按币种分列合计：" + summary);
        if (summary.Contains("115.00") || summary.Contains("109.00"))
            throw new InvalidOperationException("明细把不同币种相加了：" + summary);
        var columns = DetailForm.Columns(600, 96, 0, 34, "总价");
        if (columns.Count != 6 || columns[4].Text != "币种" || columns[5].Text != "总价")
            throw new InvalidOperationException("明细列未包含独立币种/总价列。");
        if (columns[0].Rect.Right > columns[5].Rect.Right)
            throw new InvalidOperationException("明细列矩形未实体化或越界。");
    }

    /// <summary>R3-2/R3-4: multi-currency text is complete, per-currency and never cross-summed.</summary>
    private static void RunMultiCurrencySummaryRegression()
    {
        var record = new DeclarationRecord
        {
            DeclarationNo = "310120260000000010",
            SourcePath = "multi.pdf",
            Status = "双引擎校验通过",
            LineTotals =
            [
                new DeclarationLineTotal { Sequence = 1, ItemNo = "1", Currency = "USD", Amount = 100m, IsReliable = true },
                new DeclarationLineTotal { Sequence = 2, ItemNo = "2", Currency = "EUR", Amount = 50m, IsReliable = false, VerificationAmount = 49m },
                new DeclarationLineTotal { Sequence = 3, ItemNo = "3", Currency = "USD", Amount = 30m, IsReliable = false }
            ]
        };
        record.Totals = DeclarationParser.SumReliableLineTotals(record.LineTotals);
        var lines = MainForm.AmountLines(record);
        if (lines.Count != 3) throw new InvalidOperationException($"多币种金额行数异常：{lines.Count}");
        if (lines.Count(x => x.Currency == "USD" && !x.Reliable) != 1 || lines.Count(x => x.Currency == "EUR" && !x.Reliable) != 1)
            throw new InvalidOperationException("未确认金额未按币种分列。");
        var copy = MainForm.CopyAmountText(record);
        if (!copy.Contains("USD 100.00") || !copy.Contains("EUR 50.00") || !copy.Contains("USD 30.00"))
            throw new InvalidOperationException("金额复制文本不完整：" + copy);
        if (copy.Contains("150.00") || copy.Contains("130.00") || copy.Contains("180.00"))
            throw new InvalidOperationException("金额复制文本把不同币种相加了：" + copy);
        var number = MainForm.CopyNumberText(record);
        if (!number.Contains(record.DeclarationNo) || !number.Contains("multi.pdf"))
            throw new InvalidOperationException("单号复制文本缺少单号或源文件：" + number);
        if (MainForm.StatusLabel(record).Length == 0)
            throw new InvalidOperationException("状态复制文本为空。");
    }

    /// <summary>R3-2: real grid selection copies No/Amount/Status with full text, not blanks.</summary>
    private static void RunCopySelectionRegression()
    {
        var previousData = Environment.GetEnvironmentVariable("CUSTOMS_CONSOLE_DATA");
        using var workspace = new TemporaryDirectory(Path.GetTempPath());
        Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", workspace.Path);
        try
        {
            var records = BuildSnapshotRecords(workspace.Path);
            var mixed = new DeclarationRecord
            {
                DeclarationNo = "310120260000000099",
                SourcePath = "mix-currency.pdf",
                Consignee = "MIX CURRENCY LTD",
                ContractNo = "MX-2026-01",
                Status = "双引擎校验通过",
                LineTotals =
                [
                    new DeclarationLineTotal { Sequence = 1, PageNumber = 1, ItemNo = "1", Currency = "EUR", Amount = 55m, IsReliable = true },
                    new DeclarationLineTotal { Sequence = 2, PageNumber = 1, ItemNo = "2", Currency = "USD", Amount = 20m, IsReliable = true }
                ]
            };
            mixed.Totals = DeclarationParser.SumReliableLineTotals(mixed.LineTotals);
            records.Add(mixed);
            BatchScanner.MarkDuplicates(records);
            new StateStore().Save(new AppState { LastFolder = workspace.Path, ScreenshotFolder = workspace.Path, Records = records });
            using var form = new MainForm { Size = new Size(1440, 900), StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0 };
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            var grid = Descendants(form).OfType<DataGridView>().Single();
            var copyColumns = new[] { "Index", "Status", "No", "Amount", "Detail", "Verify" };
            grid.ClearSelection();
            for (var row = 0; row < Math.Min(3, grid.Rows.Count); row++)
            {
                foreach (var name in copyColumns)
                {
                    var cell = grid.Rows[row].Cells[name];
                    if (string.IsNullOrWhiteSpace(Convert.ToString(cell.Value)))
                        throw new InvalidOperationException($"单元格 {name} 第 {row} 行为空，复制会丢失内容。");
                    cell.Selected = true;
                }
            }
            var (count, text) = form.ExtractCopySelection();
            if (count != copyColumns.Length * Math.Min(3, grid.Rows.Count))
                throw new InvalidOperationException($"复制选择单元格数量异常：{count}");
            if (!text.Contains(records[0].DeclarationNo) || !text.Contains(records[0].SourceName))
                throw new InvalidOperationException("复制文本缺少单号或源文件。");
            if (!text.Contains("USD") && !text.Contains("CNY") && !text.Contains("EUR"))
                throw new InvalidOperationException("复制文本缺少币种金额。");
            if (!text.Contains("重复") && !text.Contains("正常") && !text.Contains("需关注") && !text.Contains("识别失败"))
                throw new InvalidOperationException("复制文本缺少状态文本。");
            if (!text.Contains("明细") || !text.Contains("校验") && !text.Contains("已留存") && !text.Contains("不可核验"))
                throw new InvalidOperationException("复制文本缺少操作列文本。");
            if (MainForm.CopyAmountText(mixed).Contains("75.00"))
                throw new InvalidOperationException("多币种复制被跨币种相加。");
            form.Close();
        }
        finally { Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", previousData); }
    }

    private static void RunLineDetailParseRegression()
    {
        var page = new TextPage
        {
            PageNumber = 1,
            Width = 2800,
            Height = 1932,
            Tokens =
            [
                new TextToken("单价/总价/币制", 1432, 896, 1617, 930),
                new TextToken("1", 150, 940, 175, 975),
                new TextToken("冷冻鳕鱼片", 400, 940, 700, 975),
                new TextToken("12,000", 1100, 978, 1230, 1012),
                new TextToken("KG", 1240, 978, 1300, 1012),
                new TextToken("2.15", 1500, 943, 1560, 975),
                new TextToken("25800.00", 1520, 978, 1660, 1010),
                new TextToken("美元", 1600, 1015, 1660, 1050)
            ]
        };
        var record = new DeclarationParser().Parse("line-detail.png", new DocumentText { Pages = [page] });
        if (record.LineTotals.Count != 1) throw new InvalidOperationException($"分项数错误：{record.LineTotals.Count}");
        var line = record.LineTotals[0];
        if (line.ProductName != "冷冻鳕鱼片") throw new InvalidOperationException($"商品名称解析错误：{line.ProductName}");
        if (line.Quantity != 12000m) throw new InvalidOperationException($"数量解析错误：{line.Quantity}");
        if (!line.Unit.Equals("KG", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"单位解析错误：{line.Unit}");
        if (line.UnitPrice != 2.15m) throw new InvalidOperationException($"单价解析错误：{line.UnitPrice}");
        if (line.Amount != 25800m || line.Currency != "USD") throw new InvalidOperationException($"总价或币制解析错误：{line.DisplayAmount}");

        var bare = new TextPage
        {
            PageNumber = 1,
            Width = 2800,
            Height = 1932,
            Tokens =
            [
                new TextToken("单价/总价/币制", 1432, 896, 1617, 930),
                new TextToken("1", 150, 940, 175, 975),
                new TextToken("25800.00", 1520, 978, 1660, 1010),
                new TextToken("美元", 1600, 1015, 1660, 1050)
            ]
        };
        var bareRecord = new DeclarationParser().Parse("bare.png", new DocumentText { Pages = [bare] });
        var bareLine = bareRecord.LineTotals.Single();
        if (bareLine.Quantity is not null || bareLine.Unit.Length != 0 || bareLine.UnitPrice is not null)
            throw new InvalidOperationException("无法定位的数量/单位/单价必须保持为空，不得推算。");

        // A product model number separated by a large gap must never merge with the quantity.
        var spaced = new TextPage
        {
            PageNumber = 1,
            Width = 2800,
            Height = 1932,
            Tokens =
            [
                new TextToken("单价/总价/币制", 656, 896, 900, 930),
                new TextToken("2026", 290, 940, 340, 975),
                new TextToken("10", 480, 940, 520, 975),
                new TextToken("100.00", 700, 943, 780, 975),
                new TextToken("美元", 760, 1015, 820, 1050)
            ]
        };
        var spacedLine = new DeclarationParser().Parse("spaced.png", new DocumentText { Pages = [spaced] }).LineTotals.Single();
        if (spacedLine.Quantity != 10m)
            throw new InvalidOperationException($"间隔数字被错误拼接为数量：{spacedLine.Quantity}");

        // A row without descriptive values must not inherit them from the row above.
        var twoRows = new TextPage
        {
            PageNumber = 1,
            Width = 2800,
            Height = 1932,
            Tokens =
            [
                new TextToken("单价/总价/币制", 1432, 700, 1617, 734),
                new TextToken("1", 150, 740, 175, 775),
                new TextToken("商品一", 400, 740, 700, 775),
                new TextToken("100", 1100, 778, 1180, 812),
                new TextToken("KG", 1190, 778, 1250, 812),
                new TextToken("1.00", 1500, 745, 1560, 777),
                new TextToken("100.00", 1520, 780, 1660, 812),
                new TextToken("美元", 1600, 815, 1660, 850),
                new TextToken("2", 150, 900, 175, 935),
                new TextToken("200.00", 1520, 940, 1660, 972),
                new TextToken("美元", 1600, 975, 1660, 1010)
            ]
        };
        var twoRowRecord = new DeclarationParser().Parse("two-rows.png", new DocumentText { Pages = [twoRows] });
        if (twoRowRecord.LineTotals.Count != 2) throw new InvalidOperationException($"两行明细数量错误：{twoRowRecord.LineTotals.Count}");
        var secondRow = twoRowRecord.LineTotals[1];
        if (secondRow.Quantity is not null || secondRow.Unit.Length != 0 || secondRow.ProductName.Length != 0 || secondRow.UnitPrice is not null)
            throw new InvalidOperationException("下一行明细错误继承了上一行的数量/单位/商品/单价。");
    }

    private static void RunExcelExportRegression()
    {
        var folder = Path.Combine(Path.GetTempPath(), "customs-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var records = ExportValidation.BuildSyntheticBatch(12);
            var file = Path.Combine(folder, "export.xlsx");
            ExcelListExporter.Save(file, records, new DateTime(2026, 9, 24, 9, 0, 0));
            var issues = ExcelListExporter.Validate(file, records);
            if (issues.Count > 0) throw new InvalidOperationException("xlsx 校验失败：" + string.Join("；", issues));
            using var archive = System.IO.Compression.ZipFile.OpenRead(file);
            foreach (var part in new[] { "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/worksheets/sheet3.xml" })
                if (archive.GetEntry(part) is null) throw new InvalidOperationException($"xlsx 缺少部件：{part}");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    private static void RunLineTotalSafetyRegression()
    {
        var lines = new List<DeclarationLineTotal>
        {
            new() { Sequence = 1, PageNumber = 1, ItemNo = "1", Currency = "CNY", Amount = 100m, IsReliable = true },
            new() { Sequence = 2, PageNumber = 1, ItemNo = "2", Currency = "CNY", Amount = 999m, IsReliable = false }
        };
        var totals = DeclarationParser.SumReliableLineTotals(lines);
        if (totals.GetValueOrDefault("CNY") != 100m)
            throw new InvalidOperationException("双引擎冲突金额被错误计入合计。");
    }

    private static void RunMarkdownExportRegression()
    {
        var longName = new string('名', 90) + "_公司|A&B<script>";
        var first = new DeclarationRecord
        {
            DeclarationNo = "310120260000000001", Consignee = longName, ContractNo = "AB_001\n第二行",
            ExitCustoms = "大连湾海关", DestinationCountry = "美国", IsDuplicate = true, SourcePath = "sample.pdf",
            LineTotals =
            [
                new() { PageNumber = 1, Sequence = 1, ItemNo = "1", ProductName = "冷冻鳕鱼片", Quantity = 12000, Unit = "KG", UnitPrice = 2.15m, Currency = "USD", Amount = 1234567890.12m },
                new() { PageNumber = 2, Sequence = 2, ItemNo = "2", Currency = "CNY", Amount = 200m },
                new() { PageNumber = 2, Sequence = 3, ItemNo = "3", Currency = "CNY", Amount = 999m, VerificationAmount = 998m, IsReliable = false }
            ],
            Totals = new() { ["USD"] = 1234567890.12m, ["CNY"] = 200m }
        };
        var duplicate = new DeclarationRecord { DeclarationNo = first.DeclarationNo, IsDuplicate = true, SourcePath = "duplicate.pdf" };
        var result = MarkdownListExporter.Render([first, duplicate], new DateTime(2026, 9, 8, 12, 0, 0));
        if (!result.Contains(new string('名', 90)) || !result.Contains("&#95;公司&#124;A&amp;B&lt;script&gt;") ||
            !result.Contains("AB&#95;001<br>第二行") || !result.Contains("1,234,567,890.12") ||
            !result.Contains("未确认，未计入总价") || !result.Contains("998.00") ||
            !result.Contains("未保存分项价格") || !result.Contains("未识别，未计入合计") ||
            !result.Contains("冷冻鳕鱼片") || !result.Contains("12,000 KG") || !result.Contains("2.15") ||
            !result.Contains("出境关别") || !result.Contains("重复处理") ||
            result.Split("## 关单 ").Length != 3 || result.Split(Environment.NewLine + "---" + Environment.NewLine).Length != 2)
            throw new InvalidOperationException("Markdown 导出未完整保留长内容、多币种、明细字段、重复文件或未确认金额。");
        var totalPart = result.Split("### 关单总价")[1].Split("---" + Environment.NewLine)[0];
        if (totalPart.Contains("999.00") || !totalPart.Contains("200.00") || !totalPart.Contains("USD"))
            throw new InvalidOperationException("Markdown 总价错误包含未确认分项或丢失币种。");

        var folder = Path.Combine(Path.GetTempPath(), "customs-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "列表导出.md");
        try
        {
            MarkdownListExporter.Save(file, [first, duplicate], new DateTime(2026, 9, 8, 12, 0, 0));
            if (File.ReadAllText(file) != result) throw new InvalidOperationException("导出文件内容与生成结果不一致。");
            MarkdownListExporter.Save(file, [duplicate], new DateTime(2026, 9, 8, 12, 0, 0));
            if (Directory.GetFiles(folder).Length != 1 || File.ReadAllText(file) != MarkdownListExporter.Render([duplicate], new DateTime(2026, 9, 8, 12, 0, 0)))
                throw new InvalidOperationException("导出文件替换或临时文件清理失败。");
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }

    private static void RunSplitAmountAndFuzzyCurrencyRegression()
    {
        static TextPage Page(params TextToken[] tokens) => new() { Width = 2800, Height = 1932, Tokens = [.. tokens] };
        var primary = Page(
            new TextToken("20670.", 1515, 951, 1599, 974),
            new TextToken("0000", 1606, 951, 1669, 974),
            new TextToken("20670.", 1545, 987, 1629, 1010),
            new TextToken("00", 1636, 987, 1669, 1025),
            new TextToken("美", 1610, 1032, 1635, 1039));
        var secondary = Page(
            new TextToken("单价/总价/币制", 1432, 896, 1617, 930),
            new TextToken("20670.0000", 1510, 943, 1673, 980),
            new TextToken("20670.00", 1537, 976, 1674, 1017),
            new TextToken("上米", 1597, 1008, 1678, 1057));
        var record = new DeclarationParser().Parse("currency-regression.png", new DocumentText
        {
            Pages = [primary],
            VerificationPages = [secondary],
            UsedOcr = true,
            SecondaryOcrAttempted = true
        });
        if (!record.Totals.TryGetValue("USD", out var amount) || amount != 20670m)
            throw new InvalidOperationException($"分段金额或模糊币制回归失败：{record.DisplayTotal}");
    }

    private static void RunDropDownLifecycleRegression()
    {
        using var host = new Form { Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0 };
        var dropDown = new ModernDropDown { Location = new Point(10, 10), Size = new Size(140, 28), ItemHeight = 30, OpenUpward = true };
        dropDown.Items.AddRange(["50 条", "100 条", "200 条"]);
        dropDown.SelectedIndex = 0;
        var search = new TextBox { Location = new Point(10, 80), Size = new Size(260, 32) };
        host.Controls.AddRange([dropDown, search]);
        host.Show();
        Application.DoEvents();
        var showOptions = typeof(ModernDropDown).GetMethod("ShowOptions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到下拉菜单打开方法。");
        var popupField = typeof(ModernDropDown).GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到下拉菜单弹层。");
        for (var i = 0; i < 100; i++)
        {
            showOptions.Invoke(dropDown, null);
            Application.DoEvents();
            if (popupField.GetValue(dropDown) is not Form popup || popup.IsDisposed || !popup.Visible)
                throw new InvalidOperationException("下拉菜单打开后被意外销毁。");
            host.Activate();
            search.Focus();
            Application.DoEvents();
            if (popup.Visible || popup.IsDisposed)
                throw new InvalidOperationException("点击下拉菜单外部后，弹层未安全隐藏或被错误释放。");
        }
        host.Close();
    }

    private static void RunCaptionStateRegression()
    {
        using var host = new Form { Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0 };
        var maximize = new WindowCaptionButton(CaptionGlyph.Maximize);
        host.Controls.Add(maximize);
        host.Show();
        host.WindowState = FormWindowState.Maximized;
        Application.DoEvents();
        maximize.RefreshWindowState();
        if (!maximize.ShowsRestoreGlyph || maximize.AccessibleName != "还原窗口")
            throw new InvalidOperationException("窗口最大化后未切换为还原图标。");
        host.WindowState = FormWindowState.Normal;
        Application.DoEvents();
        maximize.RefreshWindowState();
        if (maximize.ShowsRestoreGlyph || maximize.AccessibleName != "最大化")
            throw new InvalidOperationException("窗口恢复后未切回最大化图标。");
        host.Close();
    }

    private static void RunEnterpriseLayoutRegression()
    {
        var previousData = Environment.GetEnvironmentVariable("CUSTOMS_CONSOLE_DATA");
        using var workspace = new TemporaryDirectory(Path.GetTempPath());
        Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", workspace.Path);
        try
        {
            var records = BuildSnapshotRecords(workspace.Path);
            new StateStore().Save(new AppState { LastFolder = workspace.Path, ScreenshotFolder = workspace.Path, Records = records });
            foreach (var size in new[] { new Size(1200, 720), new Size(1600, 1000) })
            {
                using var form = new MainForm { Size = size, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0 };
                form.Show();
                form.PerformLayout();
                Application.DoEvents();
                var controls = Descendants(form).ToList();
                var grid = controls.OfType<DataGridView>().Single();
                if (grid.ColumnCount != 11) throw new InvalidOperationException("记录表列数不是 11。");
                if (grid.Rows.Count != records.Count) throw new InvalidOperationException("记录表行数与批次不一致。");
                if (grid.Columns["No"].Width < 150) throw new InvalidOperationException("单号列宽不足。");
                if (size.Width == 1200 && (!grid.Columns["PortDest"].Visible || grid.Columns["Port"].Visible || grid.Columns["Dest"].Visible))
                    throw new InvalidOperationException("1200 宽未按规则合并出境关别/目的国。");
                if (size.Width == 1600 && (grid.Columns["PortDest"].Visible || !grid.Columns["Port"].Visible || !grid.Columns["Dest"].Visible))
                    throw new InvalidOperationException("1600 宽不应合并出境关别/目的国。");
                if (controls.OfType<FilterSegmented>().SingleOrDefault() is not { } segmented || segmented.AccessibleName != "记录筛选")
                    throw new InvalidOperationException("缺少筛选分段控件。");
                var kpi = controls.OfType<KpiPanel>().Single();
                if (kpi.Width < 380) throw new InvalidOperationException("KPI 面板宽度异常。");
                var money = controls.OfType<MoneySummaryPanel>().Single();
                if (money.RowCount < 2) throw new InvalidOperationException("多币种汇总行数异常。");
                if (grid.Height < 120) throw new InvalidOperationException("记录区域高度不足。");
                if (!controls.OfType<Button>().Any(x => x.AccessibleName == "导出列表") || !controls.OfType<Button>().Any(x => x.AccessibleName == "开始识别") || !controls.OfType<Button>().Any(x => x.AccessibleName == "设置"))
                    throw new InvalidOperationException("缺少标题行/顶栏按钮。");
                foreach (var pair in new (DataGridView, Control)[] { (grid, kpi), (grid, money) })
                    if (pair.Item1.Bounds.IntersectsWith(pair.Item2.Bounds))
                        throw new InvalidOperationException("记录区与统计区发生重叠。");
                form.Close();
            }
        }
        finally { Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", previousData); }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }

    // ---- snapshot evidence ----

    public static void CaptureUi(string outputPath, int width, int height)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var previousData = Environment.GetEnvironmentVariable("CUSTOMS_CONSOLE_DATA");
        using var workspace = new TemporaryDirectory(Path.GetTempPath());
        Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", workspace.Path);
        try
        {
            var records = BuildSnapshotRecords(workspace.Path);
            new StateStore().Save(new AppState { LastFolder = workspace.Path, ScreenshotFolder = workspace.Path, Records = records });
            using var form = NewSnapshotForm(width, height);
            SaveForm(form, outputPath);
        }
        finally { Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", previousData); }
    }

    public static void CaptureAllStates(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        Console.OutputEncoding = Encoding.UTF8;
        var states = new List<(string Name, string Size, string File, string Notes)>();

        void Capture(string state, string size, int width, int height, Action<string> seed, Action<MainForm>? configure = null)
        {
            var stateRoot = Path.Combine(outputFolder, "data", $"{state}-{size}");
            Directory.CreateDirectory(stateRoot);
            var previousData = Environment.GetEnvironmentVariable("CUSTOMS_CONSOLE_DATA");
            Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", stateRoot);
            try
            {
                seed(stateRoot);
                var file = Path.Combine(outputFolder, $"{state}-{size}.png");
                using var form = NewSnapshotForm(width, height);
                configure?.Invoke(form);
                SaveForm(form, file);
                var layout = Responsive.Compute(width, height);
                states.Add((state, size, Path.GetFileName(file), $"kpi={layout.KpiPanelWidth};search={layout.SearchWidth};compact={layout.CompactHeight};merged={layout.Table.PortDestMerged};dpi={form.DeviceDpi}"));
            }
            finally { Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", previousData); }
        }

        static void SeedComplete(string root)
        {
            var records = BuildSnapshotRecords(root);
            new StateStore().Save(new AppState { LastFolder = root, ScreenshotFolder = root, Records = records });
        }

        static void SeedReady(string root)
        {
            var batch = Path.Combine(root, "batch");
            Directory.CreateDirectory(batch);
            for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(batch, i < 6 ? $"scan_{i + 1:D2}.pdf" : $"scan_{i + 1:D2}.jpg"), "fixture");
            new StateStore().Save(new AppState { LastFolder = batch, ScreenshotFolder = root, Records = [] });
        }

        static void SeedEmpty(string root) => new StateStore().Save(new AppState { LastFolder = "", ScreenshotFolder = root, Records = [] });

        // Unloaded / ready / complete / filter-empty across the four required sizes.
        foreach (var (width, height, size) in new[] { (1200, 720, "1200x720"), (1280, 800, "1280x800"), (1440, 900, "1440x900"), (1920, 1080, "1920x1080") })
        {
            Capture("unloaded", size, width, height, SeedEmpty);
            Capture("ready", size, width, height, SeedReady);
            Capture("complete", size, width, height, SeedComplete);
            Capture("filter-empty", size, width, height, SeedComplete, form => form.ApplyPreviewSearch("不存在的单号ZZZ"));
        }

        // Processing uses a dedicated capture that sets the progress preview before drawing.
        foreach (var (width, height, size) in new[] { (1280, 800, "1280x800"), (1440, 900, "1440x900") })
        {
            Capture("processing", size, width, height, SeedReady, form => form.PreviewProcessing(6, 10, "scan_0921_03.jpg", 5, 0, 0));
        }

        // Real native popups over the complete workspace: the same DesignMenu popup Forms
        // the user sees are shown through the real button click, position-checked and
        // captured. They are not re-drawn at a fake location.
        var menuRoot = Path.Combine(outputFolder, "data", "menus");
        Directory.CreateDirectory(menuRoot);
        var previous = Environment.GetEnvironmentVariable("CUSTOMS_CONSOLE_DATA");
        Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", menuRoot);
        try
        {
            SeedComplete(menuRoot);
            using var form = NewSnapshotForm(1280, 800);
            form.Opacity = 1;
            form.Location = new Point(0, 0);
            form.Activate();
            Application.DoEvents();
            CaptureRealMenu(form, "导出列表", "menu-export", outputFolder, states);
            CaptureRealMenu(form, "清理", "menu-cleanup", outputFolder, states);
        }
        finally { Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", previous); }

        // Dialogs.
        CaptureDialog(Path.Combine(outputFolder, "dialog-settings.png"), "settings");
        CaptureDialog(Path.Combine(outputFolder, "dialog-detail.png"), "detail");
        CaptureDialog(Path.Combine(outputFolder, "dialog-detail-conflict.png"), "detail-conflict");
        CaptureDialog(Path.Combine(outputFolder, "dialog-cleanup-docs-step1.png"), "cleanup-docs-1");
        CaptureDialog(Path.Combine(outputFolder, "dialog-cleanup-docs-step2.png"), "cleanup-docs-2");
        CaptureDialog(Path.Combine(outputFolder, "dialog-cleanup-list.png"), "cleanup-list");
        states.Add(("dialog", "-", "dialog-settings.png", "设置 520x400"));
        states.Add(("dialog", "-", "dialog-detail.png", "明细 600x480"));
        states.Add(("dialog", "-", "dialog-detail-conflict.png", "明细冲突"));
        states.Add(("dialog", "-", "dialog-cleanup-docs-step1.png", "关单清理 1/2"));
        states.Add(("dialog", "-", "dialog-cleanup-docs-step2.png", "关单清理 2/2"));
        states.Add(("dialog", "-", "dialog-cleanup-list.png", "列表清理"));

        var probe = ProbeDpi();
        var report = new
        {
            generatedAt = DateTime.Now.ToString("s"),
            actualDeviceDpi = probe.FormDpi,
            dpiProbe = new { formDeviceDpi = probe.FormDpi, windowDpi = probe.WindowDpi, systemDpi = probe.SystemDpi, threadContext = "PER_MONITOR_AWARE_V2" },
            windowDpiNote = $"本机实际 DeviceDpi={probe.FormDpi}（window={probe.WindowDpi}，system={probe.SystemDpi}）；四档逻辑尺寸快照按该实际 DPI 渲染。其余 DPI 档位由 UiScale/Responsive 断言覆盖；真实 125/150/200% 显示器缩放仍是人工验收项，未伪造。",
            states = states.Select(x => new { state = x.Name, size = x.Size, file = x.File, notes = x.Notes })
        };
        File.WriteAllText(Path.Combine(outputFolder, "ui-states.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"UI_STATES_OK · {states.Count} snapshots → {outputFolder}");
    }

    private static MainForm NewSnapshotForm(int width, int height)
    {
        var form = new MainForm
        {
            Size = new Size(Math.Max(1200, width), Math.Max(720, height)),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        return form;
    }

    private static Bitmap RenderForm(MainForm form)
    {
        form.PerformLayout();
        Application.DoEvents();
        var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        return bitmap;
    }

    private static void SaveForm(MainForm form, string path)
    {
        using var bitmap = RenderForm(form);
        bitmap.Save(path, ImageFormat.Png);
        form.Close();
    }

    /// <summary>
    /// Clicks the real toolbar button, waits for the real DesignMenu popup Form, asserts
    /// its screen bounds stay inside the working area, and captures the popup itself plus
    /// a composite over the workspace. The popup is never re-drawn at a fixed origin.
    /// </summary>
    private static void CaptureRealMenu(MainForm form, string buttonAccessibleName, string name, string outputFolder,
        List<(string Name, string Size, string File, string Notes)> states)
    {
        var button = Descendants(form).OfType<Button>().FirstOrDefault(x => x.AccessibleName == buttonAccessibleName)
            ?? throw new InvalidOperationException($"找不到工具栏按钮：{buttonAccessibleName}");
        button.PerformClick();
        Application.DoEvents();
        var popup = Application.OpenForms.Cast<Form>()
            .FirstOrDefault(candidate => !ReferenceEquals(candidate, form) && candidate.Visible && candidate.Width > 0 && candidate.Height > 0)
            ?? throw new InvalidOperationException($"菜单 {name} 未真实弹出。");
        try
        {
            var working = Screen.FromControl(button).WorkingArea;
            var anchor = button.RectangleToScreen(button.ClientRectangle);
            var bounds = popup.Bounds;
            if (!working.Contains(bounds))
                throw new InvalidOperationException($"菜单 {name} 弹出位置超出工作区：popup={bounds} working={working}");
            var notes = $"真实弹出 bounds={bounds};anchor={anchor};working={working};popupDpi={popup.DeviceDpi};anchorDpi={button.DeviceDpi}";
            using (var popupBitmap = new Bitmap(popup.Width, popup.Height))
            {
                popup.DrawToBitmap(popupBitmap, new Rectangle(Point.Empty, popup.Size));
                popupBitmap.Save(Path.Combine(outputFolder, $"{name}-popup.png"), ImageFormat.Png);
                using var composite = RenderForm(form);
                using (var graphics = Graphics.FromImage(composite))
                    graphics.DrawImage(popupBitmap, popup.Left - form.Left, popup.Top - form.Top);
                composite.Save(Path.Combine(outputFolder, $"{name}-1440x900.png"), ImageFormat.Png);
            }
            states.Add((name, "1280x800", $"{name}-1440x900.png", notes));
            states.Add((name + "-popup", "-", $"{name}-popup.png", notes));
        }
        finally
        {
            popup.Close();
            Application.DoEvents();
        }
    }

    /// <summary>R3-7: exercises the real DesignMenu hit-test/selection event path.</summary>
    private static void RunDesignMenuInteractionRegression()
    {
        string? chosen = null;
        using var menu = new DesignMenu([new DesignMenu.Entry("excel", "导出为 Excel", null, Ui2.FileExcel, ".xlsx")]);
        menu.ItemSelected += (_, key) => chosen = key;
        using var host = new Form { Size = new Size(420, 300), StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0 };
        var anchor = new Button { Text = "打开菜单", Location = new Point(24, 24), Size = new Size(96, 32) };
        host.Controls.Add(anchor);
        host.Show();
        try
        {
            menu.Show(anchor, anchor.Height + 6, true);
            Application.DoEvents();
            if (!menu.Visible) throw new InvalidOperationException("DesignMenu 未真实显示。");
            var surfaceField = typeof(DesignMenu).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DesignMenu 缺少 _surface。");
            var surface = (Control?)surfaceField.GetValue(menu) ?? throw new InvalidOperationException("DesignMenu 未创建弹出表面。");
            var rowsField = surface.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MenuSurface 缺少 _rows。");
            var hoverField = surface.GetType().GetField("_hover", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MenuSurface 缺少 _hover。");
            var rows = (List<Rectangle>?)rowsField.GetValue(surface) ?? throw new InvalidOperationException("MenuSurface 行矩形缺失。");
            if (rows.Count != 1) throw new InvalidOperationException($"MenuSurface 行数异常：{rows.Count}");
            hoverField.SetValue(surface, 0);
            var onMouseUp = surface.GetType().GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MenuSurface 缺少 OnMouseUp。");
            onMouseUp.Invoke(surface, [new MouseEventArgs(MouseButtons.Left, 1, rows[0].X + 4, rows[0].Y + 4, 0)]);
            Application.DoEvents();
            if (chosen != "excel") throw new InvalidOperationException($"菜单点击未触发选择事件：{chosen ?? "null"}");
            menu.Close();
        }
        finally { host.Close(); }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    /// <summary>
    /// Reads the real DPI of a probe window under a PER_MONITOR_AWARE_V2 thread context.
    /// A cloud runner usually reports 96; the report keeps that honest instead of
    /// hard-coding a value.
    /// </summary>
    private static (int FormDpi, uint WindowDpi, uint SystemDpi) ProbeDpi()
    {
        var previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        try
        {
            var system = GetDpiForSystem();
            using var probe = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                ShowInTaskbar = false,
                Opacity = 0
            };
            probe.Show();
            var formDpi = probe.DeviceDpi;
            var windowDpi = GetDpiForWindow(probe.Handle);
            probe.Close();
            return (formDpi, windowDpi, system);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    public static void CaptureDialog(string outputPath, string kind)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var record = BuildSnapshotRecords(Path.GetTempPath()).First();
        var conflict = BuildConflictRecord();
        using Form form = kind.ToLowerInvariant() switch
        {
            "settings" => new DirectorySettingsForm(@"D:\关单\2026-09\第3批", @"D:\关单\核验截图"),
            "detail" => new DetailForm(record),
            "detail-conflict" => new DetailForm(conflict),
            "cleanup-docs-1" => new CleanupDialog(CleanupKind.Declarations, 24, 0, @"C:\业务资料\出口业务\待处理关单"),
            "cleanup-docs-2" => StepTwoCleanupDialog(CleanupKind.Declarations, 24, @"C:\业务资料\出口业务\待处理关单"),
            "cleanup-screenshots-1" => new CleanupDialog(CleanupKind.Screenshots, 12, 0, @"D:\关单\核验截图"),
            "cleanup-screenshots-2" => StepTwoCleanupDialog(CleanupKind.Screenshots, 12, @"D:\关单\核验截图"),
            "cleanup-list" => new CleanupDialog(CleanupKind.List, 0, 10, ""),
            _ => new CleanupDialog(CleanupKind.List, 0, 10, "")
        };
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(outputPath, ImageFormat.Png);
        form.Close();
    }

    private static CleanupDialog StepTwoCleanupDialog(CleanupKind kind, int count, string folder)
    {
        var dialog = new CleanupDialog(kind, count, 0, folder);
        var field = typeof(CleanupDialog).GetField("_stepIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var render = typeof(CleanupDialog).GetMethod("RenderStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(dialog, 2);
        render.Invoke(dialog, null);
        return dialog;
    }

    public static void WriteEnvironmentReport(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var probe = ProbeDpi();
        var fontCheck = AppFonts.VerifyShipset();
        var dpiBands = new[] { 96, 120, 144, 192 }.Select(dpi =>
        {
            var logical = Responsive.Compute(1200, 720);
            var wide = Responsive.Compute(1600, 1000);
            return new
            {
                dpi,
                scale = dpi / 96.0,
                px24 = UiScale.Px(dpi, 24),
                compactAt720 = logical.CompactHeight,
                mergedAt1200 = logical.Table.PortDestMerged,
                mergedAt1600 = wide.Table.PortDestMerged
            };
        }).ToArray();
        var report = new
        {
            version = typeof(SelfTest).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.ToString(),
            isWindows = OperatingSystem.IsWindows(),
            dpi = probe.FormDpi,
            dpiProbe = new
            {
                formDeviceDpi = probe.FormDpi,
                windowDpi = probe.WindowDpi,
                systemDpi = probe.SystemDpi,
                threadContext = "PER_MONITOR_AWARE_V2"
            },
            realMonitorScalingVerified = probe.FormDpi != 96,
            dpiNote = probe.FormDpi == 96
                ? "云端 runner 实际 DeviceDpi=96。UiScale/Responsive 的 120/144/192 断言覆盖缩放算式，但真实 125/150/200% 显示器缩放未在此环境验证，保留为实机人工验收项。"
                : $"云端 runner 实际 DeviceDpi={probe.FormDpi}，快照与控件按该 DPI 真实渲染。",
            fontVerification = new { ok = fontCheck.Ok, detail = fontCheck.Detail },
            uiFamilyLoaded = AppFonts.HasUiFamily,
            monoFamilyLoaded = AppFonts.HasMonoFamily,
            loadedFamilies = AppFonts.LoadedFamilies,
            primaryUrl = BrowserValidation.PrimaryUrl,
            browser = TryBrowser(),
            assets = new
            {
                appIcon = File.Exists(Path.Combine(AppContext.BaseDirectory, "app.ico")),
                uiV2Icons = UiV2Icons.AssetExists(Ui2.Play, 24),
                fontsDirectory = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "fonts")),
                fontFiles = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "fonts"))
                    ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fonts")).Select(Path.GetFileName).ToArray()
                    : [],
                licenses = new
                {
                    rootLicense = File.Exists(Path.Combine(AppPaths.BaseDirectory, "LICENSE")),
                    thirdPartyNotices = Directory.Exists(Path.Combine(AppPaths.BaseDirectory, "third-party-notices")),
                    about = File.Exists(Path.Combine(AppContext.BaseDirectory, "AboutAndLicenses.txt")),
                    notoOfl = File.Exists(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansSC-OFL.txt")),
                    jetBrainsOfl = File.Exists(Path.Combine(AppContext.BaseDirectory, "fonts", "JetBrainsMono-OFL.txt"))
                }
            }
        };
        File.WriteAllText(Path.Combine(outputFolder, "environment.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!fontCheck.Ok)
        {
            Console.Error.WriteLine("FONT_VERIFICATION_FAILED: " + fontCheck.Detail);
            Environment.ExitCode = 1;
        }
    }

    private static string TryBrowser()
    {
        try { return BrowserValidation.ResolveBrowser().DisplayName; }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }

    // ---- synthetic data ----

    private static List<DeclarationRecord> BuildSnapshotRecords(string root)
    {
        var files = new[]
        {
            ("310120260000000001", "NORDIC MARINE SUPPLY AS", "NMS-2026-0918", "大连湾海关", "挪威", "USD", 35528.00m, true, false),
            ("310120260000000001", "NORDIC MARINE SUPPLY AS", "NMS-2026-0918", "大连湾海关", "挪威", "USD", 35528.00m, true, false),
            ("310420260000742108", "PACIFIC RIM FOODS INC.", "PRF2026/0412", "宁波北仑海关", "美国", "USD", 12480.00m, false, true),
            ("090120260000091377", "KANTO INDUSTRIAL CO.,LTD.", "KI-HT-26-0877", "大连湾海关", "日本", "CNY", 371520.00m, false, false),
            ("220120260000184102", "GULF STAR GENERAL TRADING LLC", "GS/2026/115", "大窑湾海关", "阿联酋", "USD", 48960.00m, false, false),
            ("090120260000092216", "EURO PARTS GMBH", "EP-26-00319", "大连湾海关", "德国", "EUR", 22150.00m, false, false),
            ("420120260000330915", "SEOUL HANBIT FOOD CO., LTD.", "HB20260907", "青岛大港海关", "韩国", "USD", 9875.50m, false, false),
            ("310120260000000044", "ATLAS IMPORTS LLC", "AT-2026-0044", "宁波北仑海关", "美国", "CNY", 55200.00m, false, false),
            ("530120260000000055", "HORIZON TRADING PTE LTD", "HZ-2026-0055", "盐田海关", "新加坡", "USD", 15300.00m, false, false),
            ("220120260000000066", "SAHARA LOGISTICS FZE", "SL-2026-0066", "洋山港区海关", "阿联酋", "EUR", 8120.00m, false, false)
        };
        var records = new List<DeclarationRecord>();
        for (var i = 0; i < files.Length; i++)
        {
            var (number, consignee, contract, customs, country, currency, amount, duplicate, attention) = files[i];
            var source = Path.Combine(root, i == 1 ? "关单_0918_A(1).pdf" : $"scan_{i + 1:D4}_declaration.pdf");
            TryWriteFixture(source);
            var lineCurrency = currency;
            var lineTotal = new DeclarationLineTotal
            {
                Sequence = 1,
                PageNumber = 1,
                ItemNo = "1",
                ProductName = i % 3 == 0 ? "冷冻鳕鱼片" : i % 3 == 1 ? "脱水蔬菜" : "复合调味料",
                Quantity = 1200 + i * 10,
                Unit = i % 2 == 0 ? "KG" : "台",
                UnitPrice = Math.Round(amount / (1200 + i * 10), 4),
                Currency = lineCurrency,
                Amount = amount,
                IsReliable = !attention,
                VerificationAmount = attention ? amount - 300m : null,
                VerificationQuantity = attention ? 1200m : null,
                VerificationUnit = attention ? "KG" : "",
                VerificationUnitPrice = attention ? 4.00m : null,
                VerificationProductName = attention ? "复合调味料（复核）" : "",
                Note = attention ? "双引擎不一致：主 12480.00；复核 12180.00，未计入合计" : "双引擎一致"
            };
            var record = new DeclarationRecord
            {
                DeclarationNo = number,
                Consignee = consignee,
                ContractNo = contract,
                ExitCustoms = customs,
                DestinationCountry = country,
                SourcePath = source,
                Status = attention ? "需关注" : "双引擎校验通过",
                Warning = attention ? "金额双引擎不一致" : "",
                Confidence = 90,
                ScannedAt = new DateTime(2026, 9, 24, 14, 32, 0),
                LineTotals = [lineTotal]
            };
            record.Totals = attention ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [currency] = amount };
            if (i % 4 == 0)
            {
                var shot = Path.Combine(root, $"{number}.png");
                TryWritePng(shot);
                record.ScreenshotPath = shot;
            }
            records.Add(record);
        }
        BatchScanner.MarkDuplicates(records);
        return records;
    }

    private static DeclarationRecord BuildConflictRecord()
    {
        var record = new DeclarationRecord
        {
            DeclarationNo = "310420260000742108",
            Consignee = "PACIFIC RIM FOODS INC.",
            ContractNo = "PRF2026/0412",
            SourcePath = "scan_0921_03.jpg",
            Status = "需关注",
            Warning = "金额双引擎不一致",
            LineTotals =
            [
                new DeclarationLineTotal { Sequence = 1, PageNumber = 1, ItemNo = "1", ProductName = "脱水蔬菜", Quantity = 2400, Unit = "KG", UnitPrice = 3.20m, Currency = "USD", Amount = 7680.00m, IsReliable = true, Note = "双引擎一致" },
                new DeclarationLineTotal { Sequence = 2, PageNumber = 1, ItemNo = "2", ProductName = "复合调味料", Quantity = 1200, Unit = "KG", UnitPrice = 4.00m, Currency = "USD", Amount = 4800.00m, VerificationAmount = 4750.00m, IsReliable = false, Note = "双引擎不一致，未计入合计" }
            ]
        };
        record.Totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 7680.00m };
        return record;
    }

    private static void TryWriteFixture(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    private static void TryWritePng(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bitmap = new Bitmap(8, 8);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    public static async Task DumpOcrAsync(string path)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var document = await new DocumentExtractor().ExtractAsync(path, CancellationToken.None);
        static object Page(TextPage page) => new
        {
            page.Width,
            page.Height,
            tokens = page.Tokens
                .OrderBy(x => x.CenterY)
                .ThenBy(x => x.Left)
                .Select(x => new { x.Text, x.Left, x.Top, x.Right, x.Bottom, x.Confidence })
        };
        var output = new
        {
            primary = document.Pages.Select(Page),
            secondary = document.VerificationPages.Select(Page),
            document.SecondaryOcrError
        };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunAsync(string folder)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(x => Console.Error.WriteLine($"{x.Done}/{x.Total} {x.File}"));
            var records = await new BatchScanner().ScanAsync(folder, progress, CancellationToken.None);
            var output = new
            {
                fileCount = records.Count,
                uniqueCount = records.Count(x => x.IsCanonical),
                duplicateGroups = records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count(),
                grossTotals = BatchScanner.GrossTotals(records),
                deduplicatedTotals = BatchScanner.DeduplicatedTotals(records),
                records = records.Select(x => new
                {
                    x.SourceName, x.DeclarationNo, x.Consignee, x.ContractNo, x.ExitCustoms, x.DestinationCountry,
                    x.Totals, x.LineTotals, x.Status, x.Warning, x.Confidence, x.IsDuplicate, x.IsCanonical
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = records.Any(x => x.Status is "需关注" or "识别失败") ? 2 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }

    public static async Task RunKnownRegressionAsync(string folder)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        try
        {
            var records = await new BatchScanner().ScanAsync(folder,
                new Progress<(int Done, int Total, string File)>(x => Console.Error.WriteLine($"{x.Done}/{x.Total} {x.File}")),
                CancellationToken.None);
            static DeclarationRecord One(IEnumerable<DeclarationRecord> source, string number) =>
                source.FirstOrDefault(x => x.DeclarationNo == number)
                ?? throw new InvalidOperationException($"未识别到报关单号 {number}");
            static void AssertMoney(DeclarationRecord record, decimal expected, int expectedLines)
            {
                if (!record.Totals.TryGetValue("CNY", out var actual) || actual != expected)
                    throw new InvalidOperationException($"{record.DeclarationNo} 总值错误：{record.DisplayTotal}，期望 CNY {expected:N2}");
                if (record.LineTotals.Count != expectedLines)
                    throw new InvalidOperationException($"{record.DeclarationNo} 逐项总价数量错误：{record.LineTotals.Count}，期望 {expectedLines}");
            }

            var f036 = records.Where(x => x.DeclarationNo == "310120260516523599").ToList();
            if (f036.Count != 2) throw new InvalidOperationException("重复关单未识别为两份。");
            foreach (var record in f036) AssertMoney(record, 29234.00m, 23);
            AssertMoney(One(records, "310120260516526759"), 2250.00m, 1);
            AssertMoney(One(records, "310120260516524559"), 215111.70m, 34);
            var rotated = One(records, "516620260000491860");
            AssertMoney(rotated, 989148.63m, 10);
            if (rotated.ContractNo != "BN26040013" || rotated.DestinationCountry != "泰国" ||
                !rotated.Consignee.StartsWith("WEIZHEN TECHNOLOGY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("横向混合扫描关单的关键字段仍不正确。");
            if (!rotated.LineTotals.Select(x => x.ItemNo).SequenceEqual(Enumerable.Range(1, 10).Select(x => x.ToString())))
                throw new InvalidOperationException("横向混合扫描关单的逐项序号不连续。");
            if (rotated.Status != "双引擎校验通过")
                throw new InvalidOperationException($"横向混合扫描关单仍被标记为异常：{rotated.Warning}");
            if (records.Where(x => x.DeclarationNo.StartsWith("31012026051652", StringComparison.Ordinal))
                .Any(x => x.DestinationCountry != "印度尼西亚"))
                throw new InvalidOperationException("印度尼西亚仍被错误截断。");

            var gross = BatchScanner.GrossTotals(records).GetValueOrDefault("CNY");
            var deduplicated = BatchScanner.DeduplicatedTotals(records).GetValueOrDefault("CNY");
            if (gross != 1264978.33m || deduplicated != 1235744.33m)
                throw new InvalidOperationException($"批次合计错误：去重前 {gross:N2}；去重后 {deduplicated:N2}");
            Console.WriteLine("OCR_REGRESSION_OK");
            Console.WriteLine($"5 个文件 / 4 个唯一关单 / 去重前 CNY {gross:N2} / 去重后 CNY {deduplicated:N2}");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }
}
