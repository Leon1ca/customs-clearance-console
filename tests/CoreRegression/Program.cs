using System.Diagnostics;
using System.Reflection;
using CustomsClearanceConsole;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
using var workspace = new TemporaryDirectory(Path.GetTempPath());
Environment.SetEnvironmentVariable("CUSTOMS_CONSOLE_DATA", workspace.Path);
string FileAt(string name) { var path = Path.Combine(workspace.Path, name); File.WriteAllText(path, "fixture"); return path; }
List<DeclarationLineTotal> Lines(params decimal[] amounts) => amounts.Select((amount, i) => new DeclarationLineTotal { PageNumber = 1, ItemNo = (i + 1).ToString(), Sequence = i + 1, Currency = "USD", Amount = amount }).ToList();
List<DeclarationLineTotal> Reconcile(List<DeclarationLineTotal> a, List<DeclarationLineTotal> b)
{
    var method = typeof(DeclarationParser).GetMethod("ReconcileLineTotals", BindingFlags.NonPublic | BindingFlags.Static)!;
    return (List<DeclarationLineTotal>)method.Invoke(null, new object[] { a, b, new List<string>(), new List<string>(), 0 })!;
}
var conflict = Reconcile(Lines(100, 200, 300, 400), Lines(999));
Check(conflict.All(x => !x.IsReliable) && DeclarationParser.SumReliableLineTotals(conflict).Count == 0, "完整表格不能覆盖共有项金额冲突");
var recovered = Reconcile(Lines(100, 200, 300, 400), Lines(100));
Check(recovered.Count == 4 && recovered.All(x => x.IsReliable), "共有项一致时保留完整表格补全");
Check(recovered[0].VerificationAmount == 100 && recovered.Skip(1).All(x => x.VerificationAmount is null), "未识别复核金额保持空值");
Check(Reconcile(Lines(100), Lines(100, 200, 300, 400)).All(x => x.IsReliable), "第二引擎完整表格可补全");
var differentCurrency = Lines(100); differentCurrency[0].Currency = "CNY";
Check(Reconcile(Lines(100, 200, 300, 400), differentCurrency).All(x => !x.IsReliable), "币种冲突不进入结构补全");
var invalid = Lines(100, 200, 300, 400); invalid[3].IsReliable = false;
Check(!Reconcile(invalid, Lines(100)).All(x => x.IsReliable), "结构补全不恢复已不可靠的行");
var identified = Lines(100); identified[0].ItemNo = "2";
Check(Reconcile(Lines(100), identified).All(x => !x.IsReliable), "明确不同项号不按相近顺序误配");
var agreeing = Reconcile(Lines(100, 200), Lines(100, 201));
Check(DeclarationParser.SumReliableLineTotals(agreeing).GetValueOrDefault("USD") == 100, "仅可靠金额进入汇总");

// New descriptive fields: primary wins, missing values are recovered, no derivation.
var detailed = Lines(100, 200, 300, 400);
detailed[0].ProductName = "冷冻鳕鱼片"; detailed[0].Quantity = 100; detailed[0].Unit = "KG"; detailed[0].UnitPrice = 1m;
var secondaryDetailed = Lines(100);
secondaryDetailed[0].ProductName = "冷冻鳕鱼片（复核）"; secondaryDetailed[0].Quantity = 99; secondaryDetailed[0].Unit = "KG"; secondaryDetailed[0].UnitPrice = 1.01m;
var mergedLine = Reconcile(detailed, secondaryDetailed);
Check(mergedLine[0].ProductName == "冷冻鳕鱼片" && mergedLine[0].VerificationProductName.Contains("复核"), "主引擎明细字段优先且保留复核值");
Check(mergedLine[0].HasSecondaryDifference, "复核明细差异被标记");
var recoveredFields = Reconcile(Lines(100), secondaryDetailed);
Check(recoveredFields[0].ProductName == "冷冻鳕鱼片（复核）" && recoveredFields[0].Quantity == 99 && recoveredFields[0].UnitPrice == 1.01m, "主引擎缺失时用复核字段补全");
var bare = Lines(100);
var bareReconciled = Reconcile(bare, Lines(100));
Check(bareReconciled[0].ProductName == "" && bareReconciled[0].Quantity is null && bareReconciled[0].UnitPrice is null, "无法定位的明细字段保持空值，不推算");
var mixedPrimary = Lines(100); mixedPrimary[0].Quantity = 10;
var mismatchedUnit = Lines(100); mismatchedUnit[0].Quantity = 20; mismatchedUnit[0].Unit = "KG";
var mixed = Reconcile(mixedPrimary, mismatchedUnit);
Check(mixed[0].Quantity == 10 && mixed[0].Unit == "", "不构造两引擎都没有的数量/单位组合");
var sameQuantity = Lines(100); sameQuantity[0].Quantity = 10; sameQuantity[0].Unit = "KG";
var paired = Reconcile(mixedPrimary, sameQuantity);
Check(paired[0].Quantity == 10 && paired[0].Unit == "KG", "数量一致时才用复核单位补全");

// R5-6: a field copied from the second engine backfills the row but is not independent
// verification, and an amount read only by the second engine is never "金额一致".
var amountOnlyPrimary = Lines(100);
var fullSecondary = Lines(100);
fullSecondary[0].ProductName = "冷冻鳕鱼片"; fullSecondary[0].Quantity = 10m; fullSecondary[0].Unit = "KG"; fullSecondary[0].UnitPrice = 10m;
var backfilledLine = Reconcile(amountOnlyPrimary, fullSecondary);
Check(!backfilledLine[0].IsFullyVerified && backfilledLine[0].ItemConsistency == "未完整复核" && backfilledLine[0].AmountVerification == "金额一致",
    "复核引擎补全的字段不计为独立复核");
var secondaryOnlyPrimary = Lines(100); secondaryOnlyPrimary[0].ItemNo = "1";
var secondaryOnlySecondary = Lines(200); secondaryOnlySecondary[0].ItemNo = "2";
var secondaryOnlyLine = Reconcile(secondaryOnlyPrimary, secondaryOnlySecondary).First(x => x.Amount == 200m);
Check(secondaryOnlyLine.AmountOnlyFromSecondary && secondaryOnlyLine.AmountVerification == "金额未复核" && !secondaryOnlyLine.IsFullyVerified,
    "仅复核引擎识别的金额标注为未复核");

// R4-1: a model number sitting just left of the price column with no quantity header,
// quantity cell or unit evidence must stay out of Quantity; the source product text
// keeps the model.
var modelPage = new TextPage
{
    PageNumber = 1,
    Width = 1000,
    Height = 1400,
    Tokens =
    [
        new TextToken("单价/总价/币制", 500, 700, 640, 730),
        new TextToken("1", 50, 760, 70, 790),
        new TextToken("冷冻鳕鱼片 型号", 140, 760, 400, 790),
        new TextToken("2026", 410, 760, 460, 790),
        new TextToken("100.00", 520, 795, 640, 825),
        new TextToken("美元", 560, 835, 620, 865)
    ]
};
var modelLine = new DeclarationParser().Parse("model-token.png", new DocumentText { Pages = [modelPage] }).LineTotals.Single();
Check(modelLine.Quantity is null, "靠右数字型号在无数量列/单位证据时保持空值");
Check(modelLine.ProductName.Contains("2026"), "数量空缺时商品名称中的型号未被截断");

// A row with explicit quantity column or unit evidence is still parsed.
var unitPage = new TextPage
{
    PageNumber = 1,
    Width = 1000,
    Height = 1400,
    Tokens =
    [
        new TextToken("单价/总价/币制", 500, 700, 640, 730),
        new TextToken("1", 50, 760, 70, 790),
        new TextToken("冷冻鳕鱼片", 140, 760, 340, 790),
        new TextToken("12.5", 360, 760, 420, 790),
        new TextToken("KG", 430, 760, 470, 790),
        new TextToken("100.00", 520, 795, 640, 825),
        new TextToken("美元", 560, 835, 620, 865)
    ]
};
var unitLine = new DeclarationParser().Parse("unit-token.png", new DocumentText { Pages = [unitPage] }).LineTotals.Single();
Check(unitLine.Quantity == 12.5m && unitLine.Unit.Equals("KG", StringComparison.OrdinalIgnoreCase), "有单位证据时数量/单位仍正确解析");

// R5-5: a word that merely contains a unit substring ("HEADSET"/"RESET" contain "SET")
// is not unit evidence, so a model number next to it must not become the quantity.
TextPage ModelWithWordPage(string word) => new()
{
    PageNumber = 1,
    Width = 1000,
    Height = 1400,
    Tokens =
    [
        new TextToken("单价/总价/币制", 620, 700, 760, 730),
        new TextToken("1", 50, 760, 70, 790),
        new TextToken("WIRELESS", 140, 760, 280, 790),
        new TextToken("2026", 430, 760, 470, 790),
        new TextToken(word, 480, 760, 580, 790),
        new TextToken("100.00", 620, 795, 740, 825),
        new TextToken("美元", 660, 835, 720, 865)
    ]
};
var headsetLine = new DeclarationParser().Parse("headset-model.png", new DocumentText { Pages = [ModelWithWordPage("HEADSET")] }).LineTotals.Single();
Check(headsetLine.Quantity is null && headsetLine.Unit == "" && headsetLine.ProductName.Contains("2026"), "含 SET 子串的单词不得作为数量单位证据");
var resetLine = new DeclarationParser().Parse("reset-model.png", new DocumentText { Pages = [ModelWithWordPage("RESET")] }).LineTotals.Single();
Check(resetLine.Quantity is null && resetLine.Unit == "" && resetLine.ProductName.Contains("2026"), "RESET 等含单位子串的单词不得写入单位");

// R4-3: display and tooltip text must not round small quantities or long unit prices.
var precise = new DeclarationLineTotal { Quantity = 0.0004m, Unit = "KG", UnitPrice = 0.1234567m };
Check(precise.DisplayQuantity == "0.0004" && precise.DisplayQuantityUnit == "0.0004 KG", "明细显示的小数量不得呈现为零");
Check(precise.DisplayUnitPrice == "0.1234567", "明细显示的单价不得四舍五入");

// R4-2: the overall verdict distinguishes difference / fully verified / not verified and
// an amount conflict can never be exported as consistent.
var amountConflict = new DeclarationLineTotal
{
    Currency = "USD", ProductName = "X", Quantity = 1m, Unit = "KG", UnitPrice = 1m, Amount = 100m,
    VerificationProductName = "X", VerificationQuantity = 1m, VerificationUnit = "KG", VerificationUnitPrice = 1m,
    VerificationAmount = 99m, IsReliable = false
};
Check(amountConflict.HasAmountDifference && amountConflict.ItemConsistency == "存在差异" && amountConflict.AmountVerification == "金额不一致",
    "金额差异计入整项一致并单独标注金额不一致");
var fullyVerified = new DeclarationLineTotal
{
    Currency = "USD", ProductName = "X", Quantity = 1m, Unit = "KG", UnitPrice = 1m, Amount = 100m,
    VerificationProductName = "X", VerificationQuantity = 1m, VerificationUnit = "KG", VerificationUnitPrice = 1m, VerificationAmount = 100m
};
Check(fullyVerified.IsFullyVerified && fullyVerified.ItemConsistency == "一致" && fullyVerified.AmountVerification == "金额一致",
    "完整复核一致时才判定一致");
var partial = new DeclarationLineTotal { Currency = "USD", Amount = 100m, VerificationAmount = 100m };
Check(!partial.IsFullyVerified && partial.ItemConsistency == "未完整复核", "缺少复核字段时不得判定一致");
var legacySingle = new DeclarationLineTotal { Currency = "USD", Amount = 100m };
Check(legacySingle.ItemConsistency == "未完整复核" && legacySingle.AmountVerification == "金额未复核", "单引擎旧历史不得判定整项一致");
Check(BrowserCapturePolicy.CanBackfill("310120260000000001", "saved", "310120260000000001"), "活动会话单号一致时允许回填");
Check(!BrowserCapturePolicy.CanBackfill("310120260000000001", "saved", "310120260000000002"), "结果单号不同不得回填");
Check(!BrowserCapturePolicy.CanBackfill("310120260000000001", "error", "310120260000000001"), "失败结果不得回填");
Check(!BrowserCapturePolicy.CanBackfill("310120260000000001", "mismatch", "310120260000000001"), "单号不一致结果不得回填");
Check(TargetUrlPolicy.IsSingleWindowInquiry("https://www.singlewindow.cn/#/publicInquiryDetail?id=pi4"), "精确单一窗口查询路由被接受");
Check(!TargetUrlPolicy.IsSingleWindowInquiry("https://example.com/?singlewindow"), "含 singlewindow 子串的无关网页被拒绝");
Check(!TargetUrlPolicy.IsSingleWindowInquiry("http://www.singlewindow.cn/#/publicInquiryDetail"), "非 https 目标被拒绝");
Check(!TargetUrlPolicy.IsSingleWindowInquiry("https://www.singlewindow.cn/#/other"), "非查询路由被拒绝");
Check(!TargetUrlPolicy.IsSingleWindowInquiry("https://www.singlewindow.cn.evil.example/#/publicInquiryDetail"), "伪装主机后缀被拒绝");
// R5-4: the verified official query iframe keeps working, unrelated/unknown frames do not.
Check(TargetUrlPolicy.IsAuthorizedFrame("https://swapp.singlewindow.cn/qspserver/sw/qsp/query/view/queryDecStatus?ngBasePath=x"), "已核实的官方查询 frame 被授权");
Check(TargetUrlPolicy.IsAuthorizedFrame("https://www.singlewindow.cn/inner"), "可信主机的同源 frame 被授权");
Check(!TargetUrlPolicy.IsAuthorizedFrame("https://evil.example/qspserver/sw/qsp/query/view/queryDecStatus"), "非官方主机 frame 被拒绝");
Check(!TargetUrlPolicy.IsAuthorizedFrame("https://swapp.singlewindow.cn/other/route"), "官方主机非查询路由 frame 被拒绝");
Check(!TargetUrlPolicy.IsAuthorizedFrame("http://swapp.singlewindow.cn/qspserver/sw/qsp/query/view/queryDecStatus"), "官方查询 frame 不接受 http 降级");
Check(!TargetUrlPolicy.IsAuthorizedFrame("about:blank") && !TargetUrlPolicy.IsAuthorizedFrame(null), "未知/空 frame 来源被拒绝");
var conflictRecord = new DeclarationRecord
{
    DeclarationNo = "310120260000000001", SourcePath = "conflict.pdf", Status = "识别完成",
    LineTotals = [amountConflict], Totals = new() { ["USD"] = 100m }
};
var conflictMarkdown = MarkdownListExporter.Render([conflictRecord], new DateTime(2026, 9, 9));
Check(conflictMarkdown.Contains("另一引擎内容不同：总价 99.00"), "Markdown 标注金额差异的另一引擎总价");

var no = "310120260000000001";
var first = new DeclarationRecord { DeclarationNo = no, SourcePath = "a.pdf", Status = "需关注", Warning = "金额冲突", Confidence = 80, Totals = new() { ["USD"] = 100 } };
var second = new DeclarationRecord { DeclarationNo = no, SourcePath = "b.pdf", Status = "识别完成", Confidence = 90, Totals = new() { ["USD"] = 200 } };
var records = new List<DeclarationRecord> { first, second };
BatchScanner.MarkDuplicates(records); BatchScanner.MarkDuplicates(records);
Check(first.NeedsAttention && first.Warning == "金额冲突" && first.IsDuplicate, "重复分析幂等且不覆盖原识别异常");
Check(records.All(x => x.DuplicateWarning.Contains("不一致")), "整组重复记录均提示内容冲突");
Check(BatchScanner.GrossTotals(records)["USD"] == 300 && BatchScanner.DeduplicatedTotals(records)["USD"] == 200, "原始合计与去重合计保持独立");
records.Remove(second); BatchScanner.MarkDuplicates(records);
Check(first.IsCanonical && !first.IsDuplicate && first.DuplicateWarning == "" && first.NeedsAttention, "删除代表记录后重新计算去重和异常状态");
first.ScreenshotPath = FileAt("shot.png");
Check(first.HasScreenshot && first.DisplayStatus.Contains("需关注"), "留存截图不覆盖识别异常");
File.Delete(first.ScreenshotPath); Check(!first.HasScreenshot, "已删除截图不显示已留存");
Check(!new DeclarationRecord { DeclarationNo = new string('１', 18) }.HasValidDeclarationNo, "仅接受 18 位 ASCII 单号");

var path = FileAt("one.pdf");
var plan = ScanPlan.FromFiles(new[] { path, path, FileAt("ignored.txt") });
Check(plan.Files.Count == 1 && plan.Files[0] == Path.GetFullPath(path), "预检过滤格式并移除重复文件路径");
try { ScanPlan.FromFiles([]); Check(false, "空批次被拒绝"); } catch (InvalidOperationException) { Check(true, "空批次被拒绝"); }
var many = Enumerable.Range(0, 201).Select(i => FileAt($"many-{i}.pdf")).ToArray();
try { ScanPlan.FromFiles(many); Check(false, "超过 200 文件被拒绝"); } catch (InvalidOperationException) { Check(true, "超过 200 文件被拒绝"); }
var statePath = Path.Combine(workspace.Path, "history.json"); var store = new StateStore(statePath); store.Save(new AppState { Records = records });
try { ScanPlan.FromFiles([]); } catch (InvalidOperationException) { }
Check(store.Load().Records.Single().Warning == "金额冲突", "预检失败不修改已保存批次");
File.WriteAllText(statePath, "{\"UiSchemaVersion\":3,\"Records\":[{\"DeclarationNo\":\"310120260000000001\",\"Status\":\"重复单号\"}]}");
var migrated = store.Load();
Check(migrated.UiSchemaVersion == AppState.CurrentUiSchemaVersion && migrated.Records[0].NeedsAttention && migrated.Records[0].Warning.Contains("历史"), "旧重复记录保守迁移，不假定正常");

// Old history without the new line fields loads with empty values.
File.WriteAllText(statePath, "{\"UiSchemaVersion\":4,\"Records\":[{\"DeclarationNo\":\"310120260000000009\",\"Status\":\"识别完成\",\"LineTotals\":[{\"PageNumber\":1,\"ItemNo\":\"1\",\"Currency\":\"USD\",\"Amount\":12.5}]}]}");
var legacy = store.Load();

// An unreadable history is kept aside before the next save could overwrite it.
File.WriteAllText(statePath, "{ not json");
var corruptLoaded = store.Load();
Check(corruptLoaded.Records.Count == 0 && Directory.EnumerateFiles(workspace.Path, "history.json.corrupt-*").Any(), "无法读取的历史记录先备份再重置");
Check(legacy.Records[0].LineTotals[0].ProductName == "" && legacy.Records[0].LineTotals[0].Quantity is null && legacy.Records[0].LineTotals[0].UnitPrice is null, "旧历史缺少新字段时照常加载为空值");

var scanPlan = ScanPlan.FromFiles(new[] { path, FileAt("two.pdf") });
using var scanSession = new ScanSession(scanPlan);
var calls = 0;
var scanner = new BatchScanner((_, token) => { calls++; token.ThrowIfCancellationRequested(); return Task.FromResult(new DocumentText()); });
try
{
    await scanner.ScanAsync(scanPlan, new InlineProgress<(int Done, int Total, string File)>(_ => { }), scanSession.Token,
        new InlineProgress<DeclarationRecord>(record => { scanSession.Completed.Add(record); scanSession.Cancel(); }));
    Check(false, "取消识别保留已完成记录");
}
catch (OperationCanceledException) { Check(calls == 1 && scanSession.Completed.Count == 1, "取消识别保留已完成记录"); }
var failureScanner = new BatchScanner((_, _) => throw new IOException("读取失败"));
var failed = await failureScanner.ScanAsync(plan, new InlineProgress<(int Done, int Total, string File)>(_ => { }), CancellationToken.None);
Check(failed.Count == 1 && failed[0].Status == "识别失败", "单文件错误转为异常记录");

string tempPath;
try { using var temp = new TemporaryDirectory(workspace.Path); tempPath = temp.Path; File.WriteAllText(Path.Combine(temp.Path, "temp.png"), "data"); throw new IOException(temp.Path); }
catch (IOException ex) { tempPath = ex.Message; }
Check(!Directory.Exists(tempPath), "异常路径仍释放临时目录");
var start = OperatingSystem.IsWindows()
    ? new ProcessStartInfo("cmd", "/c ping -n 30 127.0.0.1 > nul")
    : new ProcessStartInfo("/bin/sleep", "30");
start.UseShellExecute = false; start.CreateNoWindow = true;
using var child = Process.Start(start)!; using var cancel = new CancellationTokenSource(100);
try { await OwnedProcess.WaitForExitAsync(child, cancel.Token); Check(false, "取消必须停止子进程"); }
catch (OperationCanceledException) { Check(child.HasExited, "取消必须停止子进程"); }

var exportRecord = new DeclarationRecord
{
    DeclarationNo = no, Consignee = new string('名', 90) + "|<A>", ContractNo = "AB_0001",
    ExitCustoms = "大连湾海关", DestinationCountry = "美国", Warning = "OCR提示", DuplicateWarning = "重复提示",
    LineTotals = agreeing, Totals = DeclarationParser.SumReliableLineTotals(agreeing)
};
exportRecord.LineTotals[0].ProductName = "冷冻鳕鱼片"; exportRecord.LineTotals[0].Quantity = 12000; exportRecord.LineTotals[0].Unit = "KG"; exportRecord.LineTotals[0].UnitPrice = 2.15m;
var output = MarkdownListExporter.Render([exportRecord], new DateTime(2026, 9, 9));
Check(output.Contains(new string('名', 90)) && output.Contains("&#124;&lt;A&gt;") && output.Contains("OCR提示；重复提示") && output.Contains("未确认，未计入总价"), "导出保留长内容、转义字符和独立异常提示");
Check(output.Contains("冷冻鳕鱼片") && output.Contains("12,000 KG") && output.Contains("2.15") && output.Contains("出境关别"), "Markdown 导出补齐商品/数量/单位/单价与列表字段");

// Real OOXML export + Markdown validation over an edge-case synthetic batch.
var exportReport = ExportValidation.Run(Path.Combine(workspace.Path, "export-samples"), 60);
Check(exportReport.Pass, $"Excel/Markdown 整批导出校验通过（{string.Join("；", exportReport.Issues)}）");
Check(exportReport.RecordCount >= 60 && exportReport.DetailRows > 0, "整批导出包含全部记录与分项");
var xlsx = Path.Combine(workspace.Path, "export-samples", "关单列表_合成样例.xlsx");
Check(ExcelListExporter.Validate(xlsx).Count == 0, "xlsx 重新解析无问题");

// A PDF text layer can emit XML-illegal control codes; they must not abort the whole export.
var controlRecord = new DeclarationRecord
{
    DeclarationNo = no, Consignee = "青岛\u0002海\u0001洋 😀", ContractNo = "HT\u001F-01", SourcePath = "ctrl.pdf",
    LineTotals = agreeing, Totals = DeclarationParser.SumReliableLineTotals(agreeing)
};
var controlXlsx = Path.Combine(workspace.Path, "control-chars.xlsx");
ExcelListExporter.Save(controlXlsx, [controlRecord], new DateTime(2026, 9, 9));
Check(ExcelListExporter.Validate(controlXlsx, [controlRecord]).Count == 0, "含控制字符的记录仍可导出且校验通过");
Check(ExcelListExporter.XmlSafe(controlRecord.Consignee) == "青岛海洋 😀" && ExcelListExporter.XmlSafe("\uD800x") == "x", "XML 非法字符被剔除且保留合法代理对");
using (var archive = System.IO.Compression.ZipFile.OpenRead(xlsx))
{
    var sheet1 = archive.GetEntry("xl/worksheets/sheet1.xml")!;
    using var reader = new StreamReader(sheet1.Open());
    var xml = reader.ReadToEnd();
    Check(xml.Contains("010120260000000001") && xml.Contains("=SUM(A1:A2)"), "18 位前导零与公式型外来文本按文本保存");
    Check(!xml.Contains("<v>010120260000000001</v>"), "18 位编号未保存为数值");
}
// R4-6: the quantity/unit-price cell formats must be able to display 0.0004 / 0.1234567;
// a 12-decimal format would silently round them to a visible zero.
using (var archive = System.IO.Compression.ZipFile.OpenRead(xlsx))
{
    System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var stylesDoc = System.Xml.Linq.XDocument.Load(archive.GetEntry("xl/styles.xml")!.Open());
    var customFormats = stylesDoc.Descendants(ns + "numFmt")
        .ToDictionary(x => (string?)x.Attribute("numFmtId") ?? "", x => (string?)x.Attribute("formatCode") ?? "");
    var xfs = stylesDoc.Descendants(ns + "cellXfs").Single().Elements(ns + "xf")
        .Select(x => (string?)x.Attribute("numFmtId") ?? "0").ToList();
    int DisplayCapacity(int style)
    {
        if (style < 0 || style >= xfs.Count) return -1;
        if (!customFormats.TryGetValue(xfs[style], out var code) || !code.Contains('.')) return -1;
        return code[(code.IndexOf('.') + 1)..].Count(c => c == '#');
    }
    var detailDoc = System.Xml.Linq.XDocument.Load(archive.GetEntry("xl/worksheets/sheet2.xml")!.Open());
    var numericCells = detailDoc.Descendants(ns + "c")
        .Select(c => (Value: c.Element(ns + "v")?.Value, Style: int.TryParse((string?)c.Attribute("s"), out var s) ? s : 0))
        .Where(c => c.Value is not null)
        .ToList();
    var quantityCell = numericCells.FirstOrDefault(c => c.Value == "0.0004");
    var unitPriceCell = numericCells.FirstOrDefault(c => c.Value == "0.1234567");
    Check(quantityCell.Value is not null && DisplayCapacity(quantityCell.Style) >= 4, "导出数量 0.0004 的显示格式保留足够小数位");
    Check(unitPriceCell.Value is not null && DisplayCapacity(unitPriceCell.Style) >= 7, "导出单价 0.1234567 的显示格式保留足够小数位");
}
// 目的国: layout of a real export declaration (运抵国 阿尔及利亚 (DZA), 贸易国 中国香港 (HKG),
// 指运港 斯基克达（阿尔及利亚）, goods 原产国 中国 (CHN) / 最终目的国 阿尔及利亚 (DZA)).
TextPage DeclarationPage(Func<List<TextToken>, List<TextToken>>? edit = null, string consignee = "EXAMPLE GLOBAL TRADING CO.,LIMITED")
{
    TextToken T(string text, double left, double top, double right, double bottom) => new(text, left, top, right, bottom);
    var tokens = new List<TextToken>
    {
        T("预录入编号：516620260000000017", 85, 160, 360, 178),
        T("境内发货人", 88, 193, 170, 210), T("(91000000MA00TEST0X)", 175, 193, 365, 210), T("出境关别", 557, 193, 630, 210), T("(5166)", 640, 193, 686, 210),
        T("出口日期", 833, 193, 900, 210), T("申报日期", 1120, 193, 1187, 210), T("备案号", 1380, 193, 1430, 210),
        T("示例（华中）机械制造有限公司", 88, 217, 355, 236), T("南沙新港", 557, 217, 640, 236), T("20260801", 833, 217, 910, 236), T("20260725", 1120, 217, 1197, 236),
        T("境外收货人", 88, 245, 170, 262), T("运输方式", 557, 245, 630, 262), T("(2)", 636, 245, 660, 262), T("运输工具名称及航次号", 833, 245, 990, 262), T("提运单号", 1120, 245, 1187, 262),
        T(consignee, 88, 270, 530, 288), T("水路运输", 557, 270, 640, 288), T("UN0000001/TS001W", 833, 270, 985, 288), T("000AN26L0000000M1", 1120, 270, 1280, 288),
        T("生产销售单位", 88, 295, 190, 312), T("监管方式", 557, 295, 630, 312), T("征免性质", 833, 295, 900, 312), T("许可证号", 1120, 295, 1187, 312),
        T("示例（华中）机械制造有限公司", 88, 320, 355, 338), T("一般贸易", 557, 320, 640, 338), T("一般征税", 833, 320, 910, 338), T("26-00-000001", 1120, 320, 1235, 338),
        T("合同协议号", 85, 345, 167, 362), T("贸易国（地区）", 557, 345, 683, 362), T("(HKG)", 687, 345, 722, 362),
        T("运抵国（地区）", 833, 345, 960, 362), T("(DZA)", 963, 345, 1000, 362), T("指运港", 1120, 345, 1172, 362), T("(DZA036)", 1177, 345, 1255, 362),
        T("离境口岸", 1380, 345, 1450, 362), T("(443417)", 1456, 345, 1530, 362),
        T("TESTX-DX-20260101-001", 85, 367, 290, 386), T("中国香港", 557, 367, 632, 386), T("阿尔及利亚", 833, 367, 930, 386),
        T("斯基克达（阿尔及利亚）", 1120, 367, 1325, 386), T("南沙港三期码头", 1380, 367, 1515, 386),
        T("包装种类", 88, 395, 160, 412), T("件数", 557, 395, 590, 412), T("毛重(千克)", 650, 395, 740, 412), T("净重(千克)", 833, 395, 920, 412), T("成交方式", 1005, 395, 1075, 412),
        T("项号", 88, 592, 122, 609), T("商品编号", 148, 592, 212, 609), T("商品名称及规格型号", 376, 592, 520, 609), T("数量及单位", 738, 592, 818, 609),
        T("单价/总价/币制", 923, 592, 1036, 609), T("原产国(地区)", 1098, 592, 1192, 609), T("最终目的国(地区)", 1238, 592, 1365, 609), T("境内货源地", 1468, 592, 1548, 609), T("征免", 1658, 592, 1690, 609),
        T("1", 88, 622, 95, 640), T("8703225010", 128, 622, 230, 640), T("示例牌工具车", 236, 622, 370, 640), T("4辆", 885, 622, 912, 640), T("1250.0000", 965, 622, 1052, 640),
        T("中国", 1180, 622, 1220, 640), T("阿尔及利亚", 1290, 622, 1385, 640), T("(42019)武汉其他", 1468, 622, 1618, 640), T("照章征税", 1640, 622, 1715, 640),
        T("5020千克", 836, 645, 912, 663), T("5000.00", 972, 645, 1052, 663), T("(CHN)", 1175, 645, 1225, 663), T("(DZA)", 1340, 645, 1385, 663), T("(1)", 1695, 645, 1715, 663),
        T("4辆", 885, 668, 912, 686), T("美元", 1015, 668, 1052, 686)
    };
    return new TextPage { Width = 1800, Height = 1270, Tokens = edit is null ? tokens : edit(tokens) };
}
// Real PP-OCR tokens of the declaration reported in the v1.5.2 feedback: the OCR merges each
// label with its code ("出境关别（5166）", full-width brackets) and splits 项号 from 商品编号.
TextPage RealDeclarationPage(double scale = 1)
{
    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "declaration-5166-rapidocr.json")));
    var root = json.RootElement;
    var tokens = root.GetProperty("tokens").EnumerateArray().Select(t => new TextToken(t.GetProperty("text").GetString()!,
        t.GetProperty("l").GetDouble() * scale, t.GetProperty("t").GetDouble() * scale, t.GetProperty("r").GetDouble() * scale,
        t.GetProperty("b").GetDouble() * scale, t.GetProperty("c").GetDouble() * 100)).ToList();
    return new TextPage { Width = root.GetProperty("width").GetDouble() * scale, Height = root.GetProperty("height").GetDouble() * scale, Tokens = tokens };
}
DeclarationRecord ParsePage(TextPage page) => new DeclarationParser().Parse("declaration.png", new DocumentText { UsedOcr = true, Pages = [page] });
{
    var real = ParsePage(RealDeclarationPage(2800d / 1812));
    Check(real.DeclarationNo == "516620260000000017" && real.ContractNo == "TESTX-DX-20260101-001" &&
          real.Consignee == "EXAMPLE GLOBAL TRADING CO.,LIMITED", "真实关单 OCR：报关单号、合同协议号、境外收货人");
    Check(real.ExitCustoms == "南沙新港", $"真实关单 OCR：出境关别（5166）南沙新港（实际 {real.ExitCustoms}）");
    Check(real.DestinationCountry == "阿尔及利亚", $"真实关单 OCR：目的国阿尔及利亚（实际 {real.DestinationCountry}）");
    Check(real.Totals.Count == 1 && real.Totals.TryGetValue("USD", out var usd) && usd == 5000.00m, "真实关单 OCR：总价 USD 5,000.00");
    var noBoxValue = RealDeclarationPage();
    noBoxValue.Tokens.RemoveAll(t => t.Text == "南沙新港");
    Check(ParsePage(noBoxValue).ExitCustoms == "南沙新港", "出境关别：框内值未识别时取表头海关编号后的（南沙新港）");
    Check(ParsePage(DeclarationPage()).ExitCustoms == "南沙新港", "出境关别：标签与代码分开的文本层同样识别");

    // Rules that replace the second OCR engine.
    var line = real.LineTotals.Single();
    Check(real.Status == "OCR 识别完成" && line.Quantity == 4 && line.Unit == "辆" && line.IsReliable,
        $"规则复核：OCR 合并的“4辆”读作数量，4×1250=5000 通过（{real.Status}：{real.Warning}；{line.Quantity}{line.Unit} {line.Note}）");
    TextPage Tampered(string from, string to)
    {
        var page = RealDeclarationPage(2800d / 1812);
        return new TextPage { Width = page.Width, Height = page.Height, Tokens = page.Tokens.Select(t => t.Text == from ? t with { Text = to } : t).ToList() };
    }
    var wrongTotal = ParsePage(Tampered("5000.00", "5800.00"));
    Check(wrongTotal.Status == "需关注" && wrongTotal.Warning.Contains("数量×单价与总价不符") && !wrongTotal.LineTotals.Single().IsReliable && wrongTotal.Totals.Count == 0,
        "规则复核：总价被误读（5800≠4×1250）时标为需关注且不计入合计");
    var wrongPrice = ParsePage(Tampered("1250.0000", "1260.0000"));
    Check(wrongPrice.Status == "需关注" && wrongPrice.Warning.Contains("数量×单价与总价不符"), "规则复核：单价被误读时标为需关注");
    var wrongDigit = ParsePage(Tampered("*516620260000000017*", "*516620260000000011*"));
    Check(wrongDigit.Status == "需关注" && wrongDigit.Warning.Contains("报关单号在表头读取不一致"), "规则复核：表头三处报关单号有一处读错时标为需关注");
    var wrongCountry = ParsePage(Tampered("阿尔及利亚", "阿尔及利W"));
    Check(wrongCountry.DestinationCountry == "阿尔及利亚", "规则复核：国名误读由标签国别代码纠正，不误报");
    var textLayer = new DeclarationParser().Parse("declaration.pdf", new DocumentText { Pages = [RealDeclarationPage()] });
    Check(textLayer.Status == "识别完成", "规则复核只作用于 OCR 页面，文本层 PDF 不受影响");
}
string Destination(TextPage page) => new DeclarationParser().Parse("declaration.pdf", new DocumentText { Pages = [page] }).DestinationCountry;
List<TextToken> Replace(List<TextToken> tokens, string from, string to) =>
    tokens.Select(t => t.Text == from ? t with { Text = to } : t).ToList();
List<TextToken> Without(List<TextToken> tokens, params string[] texts) => tokens.Where(t => !texts.Contains(t.Text)).ToList();

Check(Destination(DeclarationPage()) == "阿尔及利亚", "目的国：运抵国 阿尔及利亚 (DZA) 正确识别（不在旧的 21 国名单中）");
Check(Destination(DeclarationPage(consignee: "AMERICAN PARTS 美国分公司")) == "阿尔及利亚", "目的国：不再从整页任意文字（收货人里的“美国”）取国名");
Check(Destination(DeclarationPage(t => Replace(t, "阿尔及利亚", "阿尔及利W"))) == "阿尔及利亚", "目的国：OCR 误读的值由标签代码 (DZA) 纠正");
Check(Destination(DeclarationPage(t => Without(Replace(t, "阿尔及利亚", "阿尔及利亚 斯基克达（阿尔及利亚）"), "(DZA)", "指运港"))) == "阿尔及利亚",
    "目的国：指运港内容混入时取值中第一个国名");
Check(Destination(DeclarationPage(t => Replace(t, "阿尔及利亚", "").Where(x => !(x.Text == "(DZA)" && x.Top == 345)).ToList())) == "阿尔及利亚",
    "目的国：运抵国读不出时用商品表最终目的国代码");
Check(Destination(DeclarationPage(t => Replace(Replace(t, "(DZA)", "(IDN)"), "阿尔及利亚", "印度尼西亚"))) == "印度尼西亚", "目的国：印度尼西亚不被截成印度");
Check(Destination(DeclarationPage(t => Replace(Replace(t, "(DZA)", "(HKG)"), "阿尔及利亚", "中国香港"))) == "中国香港", "目的国：转运时仍取运抵国（中国香港），不取最终目的国");
Check(CountryNames.FirstNameIn("斯基克达（阿尔及利亚）") == "阿尔及利亚" && CountryNames.FromCode("dza") == "阿尔及利亚", "国别代码表与名称匹配");

Console.WriteLine($"CORE_REGRESSION_OK: {passed} checks");

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
