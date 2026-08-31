using System.Text.Json;
using System.Reflection;

namespace CustomsClearanceConsole;

internal static class SelfTest
{
    public static void RunUiContracts()
    {
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
        using (var icon24 = UiIcons.LoadButton(UiIcon.Directory, primary: false, dpi: 96F))
        using (var icon32 = UiIcons.LoadButton(UiIcon.Directory, primary: false, dpi: 144F))
        using (var icon48 = UiIcons.LoadButton(UiIcon.Directory, primary: false, dpi: 192F))
            if (icon24.Size != new Size(24, 24) || icon32.Size != new Size(32, 32) || icon48.Size != new Size(48, 48))
                throw new InvalidOperationException("按钮 PNG 图标未按 DPI 选择正确分辨率。");
        if (new[] { 24, 32, 48 }.Any(size => !File.Exists(Path.Combine(AppContext.BaseDirectory, "assets", "button-icons", $"directory-settings-{size}.png"))))
            throw new InvalidOperationException("按钮 PNG 图标资源未复制到程序目录。");
        RunSplitAmountAndFuzzyCurrencyRegression();
        RunLineTotalSafetyRegression();
        RunDropDownLifecycleRegression();
        RunCaptionStateRegression();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("UI_CONTRACTS_OK");
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
        using var details = new DeclarationDetailsForm(new DeclarationRecord
        {
            DeclarationNo = "310120260000000001",
            SourcePath = "detail-regression.pdf",
            LineTotals = lines,
            Totals = totals
        });
        if (details.Controls.Count == 0)
            throw new InvalidOperationException("金额详情窗口未正确创建。");
    }

    private static void RunSplitAmountAndFuzzyCurrencyRegression()
    {
        static TextPage Page(params TextToken[] tokens) => new()
        {
            Width = 2800,
            Height = 1932,
            Tokens = [.. tokens]
        };

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
        var dropDown = new ModernDropDown { Location = new Point(10, 10), Size = new Size(260, 52) };
        dropDown.Items.AddRange(["全部记录", "正常记录", "重复单号", "核验异常"]);
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

    public static void CaptureUi(string outputPath, int width, int height)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var form = new MainForm
        {
            Size = new Size(Math.Max(900, width), Math.Max(650, height)),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

    public static void CaptureDialog(string outputPath, string kind)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using Form form = kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
            ? new DirectorySettingsForm(@"C:\关单读取目录", @"C:\核验截图目录")
            : kind.Equals("details", StringComparison.OrdinalIgnoreCase)
                ? new DeclarationDetailsForm(new DeclarationRecord
                {
                    DeclarationNo = "516620260000491860",
                    SourcePath = @"C:\关单读取目录\横向扫描关单.pdf",
                    Totals = new() { ["CNY"] = 29234m },
                    LineTotals =
                    [
                        new() { Sequence = 1, PageNumber = 1, ItemNo = "1", Currency = "CNY", Amount = 232714.44m, VerificationAmount = 232714.44m, Note = "双引擎一致" },
                        new() { Sequence = 2, PageNumber = 1, ItemNo = "2", Currency = "CNY", Amount = 154337.14m, VerificationAmount = 154337.10m, IsReliable = false, Note = "双引擎不一致：未计入合计" },
                        new() { Sequence = 3, PageNumber = 2, ItemNo = "7", Currency = "CNY", Amount = 56096.71m, VerificationAmount = 56096.71m, Note = "双引擎一致" }
                    ]
                })
                : new ConfirmationDialog("确认清理关单目录？", "将把关单读取目录中的 PDF/图片移入 Windows 回收站。", "请确认当前目录中没有需要保留的关单文件。");
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

    public static async Task DumpOcrAsync(string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
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
