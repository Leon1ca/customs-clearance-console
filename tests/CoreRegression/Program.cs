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
Check(migrated.UiSchemaVersion == 5 && migrated.Records[0].NeedsAttention && migrated.Records[0].Warning.Contains("历史"), "旧重复记录保守迁移，不假定正常");

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

var exportRecord = new DeclarationRecord { DeclarationNo = no, Consignee = new string('名', 90) + "|<A>", Warning = "OCR提示", DuplicateWarning = "重复提示", LineTotals = agreeing, Totals = DeclarationParser.SumReliableLineTotals(agreeing) };
var output = MarkdownListExporter.Render([exportRecord], new DateTime(2026, 9, 9));
Check(output.Contains(new string('名', 90)) && output.Contains("&#124;&lt;A&gt;") && output.Contains("OCR提示；重复提示") && output.Contains("未确认，未计入总价"), "导出保留长内容、转义字符和独立异常提示");
Console.WriteLine($"CORE_REGRESSION_OK: {passed} checks");

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
