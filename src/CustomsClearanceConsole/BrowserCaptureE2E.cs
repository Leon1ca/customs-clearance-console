using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CustomsClearanceConsole;

/// <summary>
/// Controlled end-to-end harness for the in-page manual long screenshot. It serves
/// a synthetic single-window-like page over loopback, drives the visible card button
/// through real CDP mouse input, and asserts success, mismatch, too-long, double-click,
/// re-injection, tiling, frames, navigation, reconnect and mid-capture result changes.
/// It never touches the real website.
/// </summary>
internal static class BrowserCaptureE2E
{
    public sealed record Scenario(string Name, bool Pass, IReadOnlyList<string> Details);

    public static async Task<int> RunAsync(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var checks = new List<Scenario>();
        var browser = BrowserValidation.FindBrowser("Edge") ?? BrowserValidation.FindBrowser("Chrome");
        if (browser is null)
        {
            WriteReport(outputFolder, checks, ["未找到 Edge 或 Chrome，无法运行受控浏览器截图 E2E。"]);
            Console.Error.WriteLine("BROWSER_E2E_FAILED · 未找到 Edge/Chrome");
            return 1;
        }

        using var server = new TestServer();
        server.Start();
        try
        {
            checks.Add(await RunSuccessAsync(outputFolder, browser, server));
            checks.Add(await RunTilingAsync(outputFolder, browser, server));
            checks.Add(await RunMismatchAsync(outputFolder, browser, server));
            checks.Add(await RunTooLongAsync(outputFolder, browser, server));
            checks.Add(await RunDoubleClickAsync(outputFolder, browser, server));
            checks.Add(await RunReloadAsync(outputFolder, browser, server));
            checks.Add(await RunNoQueryAsync(outputFolder, browser, server));
            checks.Add(await RunCaptchaErrorAsync(outputFolder, browser, server));
            checks.Add(await RunWriteRejectedAsync(outputFolder, browser, server));
            checks.Add(await RunRepeatCaptureAsync(outputFolder, browser, server));
            checks.Add(await RunIframeAsync(outputFolder, browser, server));
            checks.Add(await RunNoClickAsync(outputFolder, browser, server));
            checks.Add(await RunMissingNumberAsync(outputFolder, browser, server));
            checks.Add(await RunStaleAsync(outputFolder, browser, server));
            checks.Add(await RunFailureRetryAsync(outputFolder, browser, server));
            checks.Add(await RunPrepareFailureRestoresAsync(outputFolder, browser, server));
            checks.Add(await RunCrossOriginFrameAsync(outputFolder, browser, server));
            checks.Add(await RunCrossSiteOopifFrameAsync(outputFolder, browser, server));
            checks.Add(await RunOfficialDomSuccessAsync(outputFolder, browser, server));
            checks.Add(await RunOfficialDomWrongNumberAsync(outputFolder, browser, server));
            checks.Add(await RunOfficialDomEmptyAsync(outputFolder, browser, server));
            checks.Add(await RunOfficialDomErrorAsync(outputFolder, browser, server));
            checks.Add(await RunOfficialDomResultChangeAsync(outputFolder, browser, server));
            checks.Add(await RunConcurrentSessionsAsync(outputFolder, browser, server));
            checks.Add(await RunNavigationReturnAsync(outputFolder, browser, server));
            checks.Add(await RunResultChangeAsync(outputFolder, browser, server));
            checks.Add(await RunReconnectAsync(outputFolder, browser, server));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            checks.Add(new Scenario("harness", false, [ex.ToString()]));
        }

        var issues = checks.Where(x => !x.Pass).SelectMany(x => x.Details).ToList();
        WriteReport(outputFolder, checks, issues);
        Console.WriteLine($"BROWSER_E2E · {checks.Count(x => x.Pass)}/{checks.Count} scenarios passed");
        foreach (var check in checks)
            Console.WriteLine($"  {(check.Pass ? "PASS" : "FAIL")}: {check.Name} · {string.Join(" | ", check.Details)}");
        return issues.Count == 0 ? 0 : 1;
    }

    private static BrowserCaptureResult Harness(BrowserValidation session, string message) =>
        new(session.SessionId, session.DeclarationNo, "harness", null, message);

    /// <summary>Starts the session, waits for the card and optionally the query result.</summary>
    private static async Task<string?> StartAndWaitAsync(BrowserValidation session, bool waitForSettled)
    {
        await session.StartAsync(CancellationToken.None);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) return "控件未注入。";
        if (waitForSettled)
        {
            var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (identity.StartsWith("waiting|", StringComparison.Ordinal) || identity.Length == 0) return $"查询结果未就绪：{identity}";
        }
        return null;
    }

    /// <summary>Performs a real mouse click on the visible card button and waits for the production verdict.</summary>
    private static async Task<BrowserCaptureResult> ClickAndAwaitAsync(BrowserValidation session, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<BrowserCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureCompleted += (_, value) => completion.TrySetResult(value);
        if (!await session.ClickCaptureButtonAsync(CancellationToken.None))
            return Harness(session, "未找到可见的卡片按钮，未执行真实点击。");
        try { return await completion.Task.WaitAsync(timeout); }
        catch (TimeoutException) { return Harness(session, "测试侧等待超时，未取得生产结论。"); }
    }

    private static async Task<BrowserCaptureResult> DriveAsync(BrowserValidation session, string? evidencePath, bool waitForSettled)
    {
        var problem = await StartAndWaitAsync(session, waitForSettled);
        if (problem is not null) return Harness(session, problem);
        if (!waitForSettled) await Task.Delay(1200);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        if (evidencePath is not null && result.State == "saved" && result.FilePath is not null && File.Exists(result.FilePath))
            File.Copy(result.FilePath, evidencePath, overwrite: true);
        return result;
    }

    private static async Task<Scenario> RunSuccessAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "success");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000001", folder,
            server.Url("/page?content=2400&result=310120260000000001"), browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, Path.Combine(outputFolder, "capture-success.png"), waitForSettled: true);
        var details = new List<string>();
        if (result.State != "saved") details.Add($"状态 {result.State}：{result.Message}");
        if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("未生成截图文件。");
        else
        {
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 2500) details.Add($"截图高度 {image.Height} 未覆盖整页（应 >= 2500）。");
            AssertPixel(image, 40, image.Height - 4, Color.FromArgb(0x12, 0x34, 0x56), "页脚标记", details);
            AssertPixel(image, 40, 40, Color.FromArgb(0x65, 0x43, 0x21), "页首标记", details);
            if (!ColumnContainsColor(image, Color.FromArgb(0xCC, 0x00, 0xCC))) details.Add("内部滚动容器末端未出现在截图中。");
            if (AnywhereContainsColor(image, Color.FromArgb(0x13, 0x35, 0x5E))) details.Add("截图中出现卡片主色，控件未被排除。");
            var restore = await session.EvaluateRawAsync("JSON.stringify(window.__cccLastCapture||null)", CancellationToken.None);
            AssertRestoreEvidence(restore, details);
            var hosts = await session.CountWidgetHostsAsync(CancellationToken.None);
            if (hosts != 1) details.Add($"控件实例数 {hosts}，应为 1。");
            if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("保存后卡片按钮不可再次点击。");
        }
        if (!session.LastClickHitTarget.Contains("host", StringComparison.OrdinalIgnoreCase))
            details.Add($"真实鼠标点击命中 {session.LastClickHitTarget}，未命中卡片按钮。");
        return new Scenario("success-fullpage", details.Count == 0, details.Count == 0 ? ["真实鼠标点击后整页首尾标记、控件排除与页面恢复均通过"] : details);
    }

    private static void AssertRestoreEvidence(string restore, List<string> details)
    {
        if (!restore.Contains("\"widgetWasHidden\":true", StringComparison.Ordinal) ||
            !restore.Contains("\"widgetHiddenAtPrepare\":true", StringComparison.Ordinal))
            details.Add($"页面未记录控件在准备时隐藏、恢复后可见：{restore}");
        if (!restore.Contains("\"stylesRestored\":true", StringComparison.Ordinal))
            details.Add($"原样式未完整恢复：{restore}");
        if (!restore.Contains("\"scrollRestored\":true", StringComparison.Ordinal))
            details.Add($"原滚动位置未完整恢复：{restore}");
        if (!restore.Contains("\"widgetVisibleAfterRestore\":true", StringComparison.Ordinal))
            details.Add($"恢复后控件未重新可见：{restore}");
    }

    private static async Task<Scenario> RunTilingAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "tiling");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000011", folder,
            server.Url("/page?content=14000&result=310120260000000011&mid=11900"), browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, Path.Combine(outputFolder, "capture-tiling.png"), waitForSettled: true);
        var details = new List<string>();
        if (result.State != "saved") details.Add($"状态 {result.State}：{result.Message}");
        if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("未生成拼接截图。");
        else
        {
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 14000) details.Add($"拼接高度 {image.Height} 不足（应覆盖 14000 以上）。");
            AssertPixel(image, 40, 11950, Color.FromArgb(0x00, 0xAA, 0x55), "接缝上侧", details);
            AssertPixel(image, 40, 12050, Color.FromArgb(0x00, 0xAA, 0x55), "接缝下侧", details);
            AssertPixel(image, 40, 40, Color.FromArgb(0x65, 0x43, 0x21), "页首标记", details);
            AssertPixel(image, 40, image.Height - 4, Color.FromArgb(0x12, 0x34, 0x56), "页尾标记", details);
        }
        return new Scenario("tiling-beyond-viewport", details.Count == 0, details.Count == 0 ? ["跨 12000px 分片拼接且接缝内容完整"] : details);
    }

    private static async Task<Scenario> RunMismatchAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "mismatch");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000002", folder,
            server.Url("/page?content=1200&result=310120260000000099"), browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, null, waitForSettled: true);
        var details = new List<string>();
        if (result.State != "mismatch") details.Add($"错误单号应拒绝保存，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("错误单号仍然生成了截图文件。");
        return new Scenario("mismatch-rejected", details.Count == 0, details.Count == 0 ? ["结果单号不一致时未保存文件"] : details);
    }

    private static async Task<Scenario> RunTooLongAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "too-long");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000003", folder,
            server.Url("/page?content=30000&result=310120260000000003"), browser, maxPixels: 300_000, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, null, waitForSettled: true);
        var details = new List<string>();
        if (result.State != "too-long") details.Add($"超长页面应拒绝，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("超长页面被裁切并保存了假成功文件。");
        return new Scenario("too-long-rejected", details.Count == 0, details.Count == 0 ? ["超过像素上限时拒绝且无文件"] : details);
    }

    private static async Task<Scenario> RunDoubleClickAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "double");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000004", folder,
            server.Url("/page?content=1500&result=310120260000000004"), browser, headless: true, allowTestTarget: true);
        var completion = new TaskCompletionSource<BrowserCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureCompleted += (_, value) => completion.TrySetResult(value);
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) return new Scenario("double-click-single-file", false, [problem]);
        await Task.WhenAll(session.ClickCaptureButtonAsync(CancellationToken.None), session.ClickCaptureButtonAsync(CancellationToken.None));
        BrowserCaptureResult result;
        try { result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (TimeoutException) { return new Scenario("double-click-single-file", false, ["测试侧等待超时，未取得生产结论。"]); }
        var files = Directory.EnumerateFiles(folder, "*.png").ToList();
        var details = new List<string>();
        if (result.State != "saved") details.Add($"状态 {result.State}：{result.Message}");
        if (files.Count != 1) details.Add($"同一次会话生成 {files.Count} 个文件，应为 1。");
        return new Scenario("double-click-single-file", details.Count == 0, details.Count == 0 ? ["重复点击只保存一次"] : details);
    }

    private static async Task<Scenario> RunReloadAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "reload");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000005", folder,
            server.Url("/page?content=900&result=310120260000000005"), browser, headless: true, allowTestTarget: true);
        await session.StartAsync(CancellationToken.None);
        var details = new List<string>();
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) details.Add("初次注入失败。");
        try { await session.EvaluateRawAsync("location.reload()", CancellationToken.None); }
        catch (Exception ex) { AppLog.Write($"E2E 刷新页面时连接重置：{ex.Message}"); }
        await Task.Delay(1500);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) details.Add("刷新后未重新注入控件。");
        else
        {
            var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (identity.StartsWith("waiting|", StringComparison.Ordinal) || identity.Length == 0)
                details.Add($"刷新后结果未就绪：{identity}");
            else
            {
                var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
                if (result.State != "saved") details.Add($"刷新后真实点击截图失败：{result.State}：{result.Message}");
                else if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("刷新后未生成截图文件。");
            }
        }
        var hosts = await session.CountWidgetHostsAsync(CancellationToken.None);
        if (hosts != 1) details.Add($"刷新后控件实例数 {hosts}，应为 1。");
        return new Scenario("reload-click-capture", details.Count == 0, details.Count == 0 ? ["刷新清理旧上下文后真实点击截图成功"] : details);
    }

    private static async Task<Scenario> RunNoQueryAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "no-query");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000006", folder,
            server.Url("/page?content=900&result=310120260000000006&query=0"), browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("no-query-rejected", false, [problem]);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State != "error") details.Add($"未查询应拒绝保存，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("未查询仍然生成了截图。");
        return new Scenario("no-query-rejected", details.Count == 0, details.Count == 0 ? ["真实点击后未查询时未保存假成功"] : details);
    }

    private static async Task<Scenario> RunCaptchaErrorAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "captcha-error");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000007", folder,
            server.Url("/page?content=900&result=310120260000000007&error=%E9%AA%8C%E8%AF%81%E7%A0%81%E9%94%99%E8%AF%AF"), browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("captcha-error-rejected", false, [problem]);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State != "error") details.Add($"验证码错误应拒绝保存，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("验证码错误仍然生成了截图。");
        return new Scenario("captcha-error-rejected", details.Count == 0, details.Count == 0 ? ["验证码错误时未保存假成功"] : details);
    }

    private static async Task<Scenario> RunWriteRejectedAsync(string outputFolder, string browser, TestServer server)
    {
        var target = Path.Combine(outputFolder, "write-rejected", "not-a-directory");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "blocking file");
        await using var session = new BrowserValidation("310120260000000008", target,
            server.Url("/page?content=900&result=310120260000000008"), browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, null, waitForSettled: true);
        var details = new List<string>();
        if (result.State != "error") details.Add($"不可写目录应报错，实际状态 {result.State}：{result.Message}");
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.png").Any()) details.Add("不可写目录仍然生成了文件。");
        return new Scenario("write-rejected", details.Count == 0, details.Count == 0 ? ["目录不可写时失败且无文件"] : details);
    }

    private static async Task<Scenario> RunRepeatCaptureAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "repeat");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000009", folder,
            server.Url("/page?content=1200&result=310120260000000009"), browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var first = await DriveAsync(session, null, waitForSettled: true);
        if (first.State != "saved") details.Add($"首次截图失败：{first.Message}");
        if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("首次截图后按钮不可用，无法重新截图。");
        var second = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        if (second.State != "saved") details.Add($"重新截图失败：{second.Message}");
        var files = Directory.EnumerateFiles(folder, "*.png").ToList();
        if (files.Count != 2) details.Add($"重新截图应生成 2 个唯一文件，实际 {files.Count}。");
        return new Scenario("repeat-capture-visible-retry", details.Count == 0, details.Count == 0 ? ["可见按钮真实点击可再次截图且文件名唯一"] : details);
    }

    private static async Task<Scenario> RunIframeAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "iframe");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000010", folder,
            server.Url("/page?content=300&result=310120260000000010&frame=1"), browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, Path.Combine(outputFolder, "capture-iframe.png"), waitForSettled: true);
        var details = new List<string>();
        if (result.State != "saved") details.Add($"同源 frame 截图失败：{result.Message}");
        if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("未生成 frame 场景截图。");
        else
        {
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 900) details.Add($"frame 内容未被完整捕获，高度 {image.Height}。");
            if (!ColumnContainsColor(image, Color.FromArgb(0x77, 0x00, 0xAA))) details.Add("frame 底部标记未出现在截图中。");
        }
        return new Scenario("same-origin-frame", details.Count == 0, details.Count == 0 ? ["同源 frame 内容与嵌套滚动被完整捕获"] : details);
    }

    private static async Task<Scenario> RunNoClickAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "no-click");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000012", folder,
            server.Url("/page?content=1200&result=310120260000000012"), browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) details.Add(problem);
        await Task.Delay(1500);
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("未点击网页按钮却生成了截图。");
        if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("未点击时卡片按钮不可用。");
        return new Scenario("no-click-no-save", details.Count == 0, details.Count == 0 ? ["未点击网页按钮不保存文件"] : details);
    }

    private static async Task<Scenario> RunMissingNumberAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "missing-number");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000013", folder,
            server.Url("/page?content=1200&result=310120260000000013&nonumber=1"), browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("missing-number-rejected", false, [problem]);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (!session.LastClickHitTarget.Contains("host", StringComparison.OrdinalIgnoreCase))
            details.Add($"缺号场景真实鼠标点击命中 {session.LastClickHitTarget}，未命中卡片按钮。");
        if (result.State != "error") details.Add($"结果区域缺少单号时应由生产路径拒绝，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("结果区域没有单号时仍生成了文件。");
        return new Scenario("missing-number-rejected", details.Count == 0, details.Count == 0 ? ["真实点击后缺少单号的生产路径拒绝保存"] : details);
    }

    private static async Task<Scenario> RunStaleAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "stale");
        Directory.CreateDirectory(folder);
        // pre=1 renders the matching result before the query, so the query changes nothing
        // and identity must report stale|unchanged instead of a false success.
        await using var session = new BrowserValidation("310120260000000014", folder,
            server.Url("/page?content=1200&result=310120260000000014&pre=1"), browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("stale-result-rejected", false, [problem]);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State != "error") details.Add($"旧结果未变化时应由生产路径拒绝，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("旧结果未变化时仍生成了文件。");
        return new Scenario("stale-result-rejected", details.Count == 0, details.Count == 0 ? ["真实点击后旧结果未变化拒绝保存"] : details);
    }

    private static async Task<Scenario> RunFailureRetryAsync(string outputFolder, string browser, TestServer server)
    {
        var parent = Path.Combine(outputFolder, "failure-retry");
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "blocked");
        File.WriteAllText(target, "blocking file");
        await using var session = new BrowserValidation("310120260000000015", target,
            server.Url("/page?content=1200&result=310120260000000015"), browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var first = await DriveAsync(session, null, waitForSettled: true);
        if (first.State == "saved") details.Add("目录不可写时首次截图意外成功。");
        File.Delete(target);
        Directory.CreateDirectory(target);
        if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("失败后卡片按钮不可重试。");
        var second = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        if (second.State != "saved") details.Add($"解除阻塞后重试失败：{second.Message}");
        if (!Directory.EnumerateFiles(target, "*.png").Any()) details.Add("重试成功后没有生成截图。");
        return new Scenario("failure-retry", details.Count == 0, details.Count == 0 ? ["失败状态可重试并成功保存"] : details);
    }

    /// <summary>
    /// Production-path failure injection for R5-2: the controlled page makes the SECOND
    /// scroll container throw on its first style write while the first container was
    /// already expanded. The prepare script must reject the capture (no file, no backfill),
    /// the C# finally must restore the already-modified styles and scroll offset, and the
    /// visible card must stay retryable; the one-shot injection then lets the retry save.
    /// </summary>
    private static async Task<Scenario> RunPrepareFailureRestoresAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "prepare-failure-restore");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000022";
        await using var session = new BrowserValidation(number, folder,
            server.Url($"/page?content=1200&result={number}&prepfail=1"), browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) return new Scenario("prepare-failure-restore", false, [problem]);
        // Give the first (successfully expanded) container a non-zero scroll offset so its
        // restoration is observable, and confirm the injected failure is armed.
        var armed = await session.EvaluateRawAsync(
            "(function(){var s=document.querySelector('#content .scroller');if(!s)return 'no-scroller';s.scrollTop=300;return 'armed:'+s.scrollTop+':'+(!!document.getElementById('bad-scroller'))+':'+(!!window.__cccPrepareFailArmed);})()",
            CancellationToken.None);
        if (!armed.Contains("armed:300:true:true", StringComparison.Ordinal)) details.Add($"受控页面未能布置失败注入：{armed}");
        var first = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        if (first.State == "saved") details.Add("准备阶段写样式失败仍保存了截图。");
        else if (first.State != "error") details.Add($"准备失败未返回错误结论：{first.State}：{first.Message}");
        else if (!first.Message.Contains("准备失败", StringComparison.Ordinal)) details.Add($"准备失败的结论不是准备错误：{first.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("准备失败仍生成了截图文件。");
        // The already-expanded container must be back to its original inline styles, scroll
        // offset and no leftover prepare ledger.
        var restored = await session.EvaluateRawAsync(
            "JSON.stringify((function(){var s=document.querySelector('#content .scroller');if(!s)return {missing:true};return {height:s.style.getPropertyValue('height'),maxHeight:s.style.getPropertyValue('max-height'),overflow:s.style.getPropertyValue('overflow-y'),scrollTop:s.scrollTop,ledger:!!window.__cccCaptureState,hidden:!!(document.getElementById('ccc-widget-host')&&document.getElementById('ccc-widget-host').style.getPropertyValue('display')==='none')};})())",
            CancellationToken.None);
        if (!restored.Contains("\"height\":\"\"", StringComparison.Ordinal) ||
            !restored.Contains("\"maxHeight\":\"\"", StringComparison.Ordinal) ||
            !restored.Contains("\"overflow\":\"\"", StringComparison.Ordinal))
            details.Add($"失败后先前修改的样式未恢复：{restored}");
        if (!restored.Contains("\"ledger\":false", StringComparison.Ordinal)) details.Add($"失败后准备账本未清理：{restored}");
        if (!restored.Contains("\"hidden\":false", StringComparison.Ordinal)) details.Add($"失败后卡片未恢复可见：{restored}");
        var scrollMatch = System.Text.RegularExpressions.Regex.Match(restored, "\"scrollTop\":(\\d+)");
        if (!scrollMatch.Success || Math.Abs(int.Parse(scrollMatch.Groups[1].Value) - 300) > 1)
            details.Add($"失败后滚动位置未恢复：{restored}");
        if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("准备失败后卡片按钮不可重试。");
        else
        {
            var second = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
            if (second.State != "saved") details.Add($"解除注入后重试失败：{second.State}：{second.Message}");
            else if (second.FilePath is null || !File.Exists(second.FilePath)) details.Add("重试成功后没有生成截图。");
        }
        return new Scenario("prepare-failure-restore", details.Count == 0,
            details.Count == 0 ? ["准备阶段局部写样式失败不保存且先前修改/滚动/卡片均恢复，可重试成功"] : details);
    }

    private static async Task<Scenario> RunCrossOriginFrameAsync(string outputFolder, string browser, TestServer server)
    {
        using var other = new TestServer();
        other.Start();
        var folder = Path.Combine(outputFolder, "cross-origin-frame");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000016";
        // The query form and the matching result live ONLY in the cross-origin frame; the
        // top-level shell has neither query nor result, like the official page. The frame is
        // additionally capped by max-height so a height-only write would truncate it (R5-3).
        var frameUrl = other.Url($"/frame?content=800&result={number}&query=1");
        var url = server.Url($"/page?content=300&result={number}&frame=cross&clamp=1&xhost={Uri.EscapeDataString(frameUrl)}");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var result = await DriveAsync(session, Path.Combine(outputFolder, "capture-cross-frame.png"), waitForSettled: true);
        var details = new List<string>();
        if (result.State != "saved") details.Add($"跨源 frame 截图失败：{result.Message}");
        if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("未生成跨源 frame 截图。");
        else
        {
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 700) details.Add($"跨源 frame 高度不足，可能被截断：{image.Height}。");
            if (!RegionContainsColor(image, new Rectangle(0, 0, image.Width, Math.Min(500, image.Height)), Color.FromArgb(0x00, 0x88, 0xCC)))
                details.Add("跨源 frame 可见顶部标记未出现在截图中。");
            if (!ColumnContainsColor(image, Color.FromArgb(0x77, 0x00, 0xAA)))
                details.Add("跨源 frame 底部标记未出现在截图中（内容被截断）。");
        }
        var style = await session.EvaluateRawAsync(
            "JSON.stringify((function(){var f=document.getElementById('inner');return {height:f.style.getPropertyValue('height'),heightPriority:f.style.getPropertyPriority('height'),maxHeight:f.style.getPropertyValue('max-height'),maxHeightPriority:f.style.getPropertyPriority('max-height')};})())",
            CancellationToken.None);
        if (!style.Contains("\"height\":\"\"", StringComparison.Ordinal) ||
            !style.Contains("\"maxHeight\":\"400px\"", StringComparison.Ordinal) ||
            !style.Contains("\"maxHeightPriority\":\"important\"", StringComparison.Ordinal))
            details.Add($"iframe 原高度/max-height 及 !important 优先级未复原：{style}");
        return new Scenario("cross-origin-frame", details.Count == 0, details.Count == 0 ? ["顶层无查询，max-height 约束的跨源 frame 内查询首尾完整且原样式优先级复原"] : details);
    }

    /// <summary>
    /// A frame served from a different registrable site is put out of process by Chromium.
    /// This proves the flattened Target session path reaches, queries, prepares, expands and
    /// captures an OOPIF query frame whose result the top-level shell never sees.
    /// </summary>
    private static async Task<Scenario> RunCrossSiteOopifFrameAsync(string outputFolder, string browser, TestServer server)
    {
        using var other = new TestServer();
        other.Start();
        const string host = "e2e-frame.test";
        var folder = Path.Combine(outputFolder, "oopif-frame");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000022";
        var frameUrl = $"http://{host}:{other.Port}/frame?content=800&result={number}&query=1";
        var url = server.Url($"/page?content=300&result={number}&frame=cross&clamp=1&xhost={Uri.EscapeDataString(frameUrl)}");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true,
            extraBrowserArgs: [$"--host-resolver-rules=MAP {host} 127.0.0.1"]);
        var result = await DriveAsync(session, Path.Combine(outputFolder, "capture-oopif-frame.png"), waitForSettled: true);
        var details = new List<string>();
        if (result.State != "saved") details.Add($"跨站 OOPIF frame 截图失败：{result.Message}");
        if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("未生成跨站 OOPIF frame 截图。");
        else
        {
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 700) details.Add($"跨站 OOPIF frame 高度不足，可能被截断：{image.Height}。");
            if (!RegionContainsColor(image, new Rectangle(0, 0, image.Width, Math.Min(500, image.Height)), Color.FromArgb(0x00, 0x88, 0xCC)))
                details.Add("跨站 OOPIF frame 顶部标记未入图。");
            if (!ColumnContainsColor(image, Color.FromArgb(0x77, 0x00, 0xAA)))
                details.Add("跨站 OOPIF frame 底部标记未入图（独立进程内容被截断）。");
        }
        var style = await session.EvaluateRawAsync(
            "JSON.stringify((function(){var f=document.getElementById('inner');return {height:f.style.getPropertyValue('height'),maxHeight:f.style.getPropertyValue('max-height'),maxHeightPriority:f.style.getPropertyPriority('max-height')};})())",
            CancellationToken.None);
        if (!style.Contains("\"height\":\"\"", StringComparison.Ordinal) ||
            !style.Contains("\"maxHeight\":\"400px\"", StringComparison.Ordinal) ||
            !style.Contains("\"maxHeightPriority\":\"important\"", StringComparison.Ordinal))
            details.Add($"OOPIF frame 原高度/max-height 及优先级未复原：{style}");
        return new Scenario("cross-site-oopif-frame", details.Count == 0, details.Count == 0 ? ["独立进程 OOPIF frame 内查询经 CDP session 完整捕获并复原样式"] : details);
    }

    /// <summary>
    /// The official query frame DOM: the top-level shell has no query, the form and the
    /// result live in the iframe, and the result is rendered as
    /// #queryDetail .display-content .content-field &gt; .field-order. The query button is
    /// triggered by a real CDP mouse click inside the frame.
    /// </summary>
    private static async Task<Scenario> RunOfficialDomSuccessAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "official-dom");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000023";
        var url = server.Url($"/page?result={number}&frame=official&mode=ok");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        var details = new List<string>();
        if (problem is not null) return new Scenario("official-dom-success", false, [problem]);
        if (!await session.ClickElementInFrameAsync("#inner", "#queryBtn", CancellationToken.None))
            details.Add("未能在 frame 内真实点击查询按钮。");
        var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!identity.StartsWith("ready|", StringComparison.Ordinal)) details.Add($"官方 DOM 查询未就绪：{identity}");
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        if (result.State != "saved") details.Add($"官方 DOM 截图失败：{result.State}：{result.Message}");
        else if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("官方 DOM 未生成截图。");
        else
        {
            File.Copy(result.FilePath, Path.Combine(outputFolder, "capture-official-dom.png"), overwrite: true);
            using var image = new Bitmap(result.FilePath);
            if (image.Height < 900) details.Add($"官方 DOM 高度不足，内容可能被截断：{image.Height}。");
            if (!RegionContainsColor(image, new Rectangle(0, 0, image.Width, Math.Min(500, image.Height)), Color.FromArgb(0x00, 0x88, 0xCC)))
                details.Add("官方 DOM 顶部标记未入图。");
            if (!ColumnContainsColor(image, Color.FromArgb(0x77, 0x00, 0xAA)))
                details.Add("官方 DOM 底部标记未入图（display-content 内容被截断）。");
        }
        return new Scenario("official-dom-success", details.Count == 0, details.Count == 0 ? ["仿官网 DOM 真实点击查询与卡片后 display-content/content-field 完整捕获"] : details);
    }

    private static async Task<Scenario> RunOfficialDomWrongNumberAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "official-dom-wrong");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000024";
        var url = server.Url($"/page?result={number}&frame=official&mode=wrong");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("official-dom-wrong-number", false, [problem]);
        await session.ClickElementInFrameAsync("#inner", "#queryBtn", CancellationToken.None);
        var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (!identity.StartsWith("mismatch|", StringComparison.Ordinal)) details.Add($"官方 DOM 错号未被识别为 mismatch：{identity}");
        if (result.State != "mismatch") details.Add($"错号结果应拒绝保存，实际 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("错号仍然生成了文件。");
        return new Scenario("official-dom-wrong-number", details.Count == 0, details.Count == 0 ? ["官方 DOM 结果单号不符时拒绝保存"] : details);
    }

    private static async Task<Scenario> RunOfficialDomEmptyAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "official-dom-empty");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000025";
        var url = server.Url($"/page?result={number}&frame=official&mode=empty");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("official-dom-empty-result", false, [problem]);
        await session.ClickElementInFrameAsync("#inner", "#queryBtn", CancellationToken.None);
        await Task.Delay(1500);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State != "error") details.Add($"空结果应拒绝保存，实际 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("空结果仍然生成了文件。");
        return new Scenario("official-dom-empty-result", details.Count == 0, details.Count == 0 ? ["官方 DOM 空结果不保存假成功"] : details);
    }

    private static async Task<Scenario> RunOfficialDomErrorAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "official-dom-error");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000026";
        var url = server.Url($"/page?result={number}&frame=official&mode=error");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("official-dom-captcha-error", false, [problem]);
        await session.ClickElementInFrameAsync("#inner", "#queryBtn", CancellationToken.None);
        await Task.Delay(1500);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State != "error") details.Add($"验证码无效应拒绝保存，实际 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("验证码无效仍然生成了文件。");
        return new Scenario("official-dom-captcha-error", details.Count == 0, details.Count == 0 ? ["官方“验证码无效/没有符合条件的数据”文案被识别为失败"] : details);
    }

    private static async Task<Scenario> RunOfficialDomResultChangeAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "official-dom-change");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000027";
        var url = server.Url($"/page?result={number}&frame=official&mode=mutate");
        await using var session = new BrowserValidation(number, folder, url, browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, false);
        if (problem is not null) return new Scenario("official-dom-result-change", false, [problem]);
        await session.ClickElementInFrameAsync("#inner", "#queryBtn", CancellationToken.None);
        var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (!identity.StartsWith("ready|", StringComparison.Ordinal)) details.Add($"官方 DOM 结果变化前未就绪：{identity}");
        if (result.State == "saved") details.Add("官方 DOM 捕获中结果变化仍保存了截图。");
        if (result.State == "harness") details.Add($"测试侧未取得生产结论：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("官方 DOM 结果变化仍生成了文件。");
        return new Scenario("official-dom-result-change", details.Count == 0, details.Count == 0 ? ["官方 DOM 捕获中 field-order 变化被撤销保存"] : details);
    }

    private static async Task<Scenario> RunConcurrentSessionsAsync(string outputFolder, string browser, TestServer server)
    {
        var root = Path.Combine(outputFolder, "concurrent");
        var folderA = Path.Combine(root, "a");
        var folderB = Path.Combine(root, "b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        await using var a = new BrowserValidation("310120260000000017", folderA,
            server.Url("/page?content=900&result=310120260000000017"), browser, headless: true, allowTestTarget: true);
        await using var b = new BrowserValidation("310120260000000018", folderB,
            server.Url("/page?content=900&result=310120260000000018"), browser, headless: true, allowTestTarget: true);
        var results = await Task.WhenAll(
            DriveAsync(a, null, waitForSettled: true),
            DriveAsync(b, null, waitForSettled: true));
        var details = new List<string>();
        foreach (var (session, folder, result) in new[] { (a, folderA, results[0]), (b, folderB, results[1]) })
        {
            if (result.State != "saved") { details.Add($"{session.DeclarationNo} 截图失败：{result.Message}"); continue; }
            var files = Directory.EnumerateFiles(folder, "*.png").ToList();
            if (files.Count != 1) details.Add($"{session.DeclarationNo} 生成 {files.Count} 个文件。");
            else if (!Path.GetFileName(files[0]).StartsWith(session.DeclarationNo, StringComparison.Ordinal))
                details.Add($"{session.DeclarationNo} 文件名 {Path.GetFileName(files[0])} 串号。");
        }
        if (Directory.EnumerateFiles(folderA, "*.png").Any(f => !Path.GetFileName(f).Contains("017"))) details.Add("会话 A 目录混入其它单号截图。");
        if (Directory.EnumerateFiles(folderB, "*.png").Any(f => !Path.GetFileName(f).Contains("018"))) details.Add("会话 B 目录混入其它单号截图。");
        return new Scenario("concurrent-session-isolation", details.Count == 0, details.Count == 0 ? ["不同会话并发截图彼此隔离"] : details);
    }

    private static async Task<Scenario> RunNavigationReturnAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "navigation");
        Directory.CreateDirectory(folder);
        const string number = "310120260000000019";
        var target = server.Url($"/page?content=900&result={number}");
        await using var session = new BrowserValidation(number, folder, target, browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) details.Add(problem);
        await session.NavigateAsync("about:blank", CancellationToken.None);
        await Task.Delay(1200);
        await session.NavigateAsync(target, CancellationToken.None);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) details.Add("返回目标页后未重新注入控件。");
        else
        {
            var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (identity.StartsWith("waiting|", StringComparison.Ordinal) || identity.Length == 0)
                details.Add($"返回目标页后结果未就绪：{identity}");
            else
            {
                var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
                if (result.State != "saved") details.Add($"导航返回后截图失败：{result.State}：{result.Message}");
                else if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("导航返回后未生成截图。");
            }
        }
        return new Scenario("navigate-away-return", details.Count == 0, details.Count == 0 ? ["离开目标页再返回后重新注入并可截图"] : details);
    }

    private static async Task<Scenario> RunResultChangeAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "result-change");
        Directory.CreateDirectory(folder);
        // The page rewrites the result shortly after the real prepare path runs, inside
        // the capture window; the save-time re-validation must abort it.
        await using var session = new BrowserValidation("310120260000000020", folder,
            server.Url("/page?content=20000&result=310120260000000020&mutate=1"), browser, headless: true, allowTestTarget: true);
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) return new Scenario("result-change-aborts-save", false, [problem]);
        var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
        var details = new List<string>();
        if (result.State == "saved") details.Add("捕获中查询结果变化仍保存了截图。");
        if (result.State == "harness") details.Add($"测试侧未取得生产结论：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("捕获中结果变化仍生成了文件。");
        return new Scenario("result-change-aborts-save", details.Count == 0, details.Count == 0 ? ["捕获中结果变化被撤销且无错误回填"] : details);
    }

    private static async Task<Scenario> RunReconnectAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "reconnect");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000021", folder,
            server.Url("/page?content=900&result=310120260000000021"), browser, headless: true, allowTestTarget: true);
        var details = new List<string>();
        var problem = await StartAndWaitAsync(session, true);
        if (problem is not null) details.Add(problem);
        session.SimulateConnectionDropForTest();
        await Task.Delay(500);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(45), CancellationToken.None)) details.Add("断连后未恢复控件。");
        else
        {
            var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (identity.StartsWith("waiting|", StringComparison.Ordinal) || identity.Length == 0)
                details.Add($"断连恢复后结果未就绪：{identity}");
            else
            {
                var result = await ClickAndAwaitAsync(session, TimeSpan.FromSeconds(120));
                if (result.State != "saved") details.Add($"断连恢复后截图失败：{result.State}：{result.Message}");
                else if (result.FilePath is null || !File.Exists(result.FilePath)) details.Add("断连恢复后未生成截图。");
            }
        }
        return new Scenario("reconnect-recovery", details.Count == 0, details.Count == 0 ? ["连接中断后恢复并可完成截图"] : details);
    }

    private static void AssertPixel(Bitmap image, int x, int y, Color expected, string label, List<string> details)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) { details.Add($"{label} 采样越界。"); return; }
        var actual = image.GetPixel(x, y);
        if (Math.Abs(actual.R - expected.R) > 6 || Math.Abs(actual.G - expected.G) > 6 || Math.Abs(actual.B - expected.B) > 6)
            details.Add($"{label} 颜色不符：{actual}。");
    }

    private static bool ColumnContainsColor(Bitmap image, Color expected)
    {
        var x = image.Width / 2;
        for (var y = 0; y < image.Height; y += 3)
        {
            var pixel = image.GetPixel(x, y);
            if (Math.Abs(pixel.R - expected.R) <= 6 && Math.Abs(pixel.G - expected.G) <= 6 && Math.Abs(pixel.B - expected.B) <= 6) return true;
        }
        return false;
    }

    private static bool AnywhereContainsColor(Bitmap image, Color expected)
    {
        for (var y = 0; y < image.Height; y += 3)
            for (var x = 0; x < image.Width; x += 3)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - expected.R) <= 6 && Math.Abs(pixel.G - expected.G) <= 6 && Math.Abs(pixel.B - expected.B) <= 6) return true;
            }
        return false;
    }

    private static bool RegionContainsColor(Bitmap image, Rectangle region, Color expected)
    {
        var left = Math.Max(0, region.Left);
        var top = Math.Max(0, region.Top);
        var right = Math.Min(image.Width, region.Right);
        var bottom = Math.Min(image.Height, region.Bottom);
        for (var y = top; y < bottom; y += 2)
            for (var x = left; x < right; x += 2)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - expected.R) <= 6 && Math.Abs(pixel.G - expected.G) <= 6 && Math.Abs(pixel.B - expected.B) <= 6) return true;
            }
        return false;
    }

    private static void WriteReport(string outputFolder, IReadOnlyList<Scenario> scenarios, IReadOnlyList<string> issues)
    {
        var report = new
        {
            pass = issues.Count == 0,
            scenarios = scenarios.Select(x => new { x.Name, x.Pass, x.Details }),
            issues
        };
        File.WriteAllText(Path.Combine(outputFolder, "browser-e2e.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>Minimal loopback HTTP server (no URL ACL required).</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private int _port;

        public void Start()
        {
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url(string path) => $"http://127.0.0.1:{_port}{path}";

        public int Port => _port;

        private async Task AcceptLoopAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer);
                    var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                    var firstLine = request.Split("\r\n").FirstOrDefault() ?? "GET / HTTP/1.1";
                    var parts = firstLine.Split(' ');
                    var path = parts.Length >= 2 ? parts[1] : "/";
                    var html = Page(path);
                    var body = Encoding.UTF8.GetBytes(html);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await stream.WriteAsync(body);
                }
            }
            catch (Exception ex) { AppLog.Write($"E2E 测试页响应失败：{ex.Message}"); }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            try { _listener.Stop(); } catch { }
            _lifetime.Dispose();
        }

        private static Dictionary<string, string> Parameters(string path)
        {
            var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
            return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2)
                .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]), StringComparer.OrdinalIgnoreCase);
        }

        private static string FormHtml(string decl, string result, bool autoQuery, string error, bool pre, bool nonumber, string extraScript)
        {
            var rowText = nonumber
                ? "查询结果已返回 海关状态 已放行 申报日期 2026-09-24 放行日期 2026-09-24"
                : $"报关单号 {result} 申报日期 2026-09-24 放行日期 2026-09-24 海关状态 已放行";
            var resultRow = error.Length > 0 ? "" : $"<tr><td>{rowText}</td></tr>";
            var initial = pre ? resultRow : "";
            var errorScript = autoQuery
                ? $"setTimeout(function () {{ document.getElementById('query').click(); document.getElementById('result').innerHTML = {JsonSerializer.Serialize(resultRow)}; }}, 400);"
                : "";
            var errorTextScript = error.Length > 0 ? $"document.getElementById('error').textContent = {JsonSerializer.Serialize(error)};" : "";
            return $$"""
              <form onsubmit="return false"><label>报关单号 <input data-customs-declaration value="{{decl}}"></label>
              <button type="button" id="query">查询</button></form>
              <div id="error"></div><div id="loading" class="loading" hidden>加载中</div>
              <div class="scroller"><div class="inner"><div id="scrollband"></div></div></div>
              <table><tbody id="result">{{initial}}</tbody></table>
              <script>
                document.querySelector('input').value = {{JsonSerializer.Serialize(decl)}};
                {{errorScript}}
                {{errorTextScript}}
                {{extraScript}}
              </script>
            """;
        }

        private static string Page(string path)
        {
            if (path.StartsWith("/blank", StringComparison.OrdinalIgnoreCase))
                return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>blank</title></head><body>blank</body></html>";
            var parameters = Parameters(path);
            var content = parameters.TryGetValue("content", out var c) && int.TryParse(c, out var height) ? height : 1200;
            var result = parameters.TryGetValue("result", out var r) ? r : "310120260000000001";
            var decl = parameters.TryGetValue("decl", out var d) ? d : result;
            var autoQuery = !parameters.TryGetValue("query", out var q) || q != "0";
            var error = parameters.TryGetValue("error", out var e) ? e : "";
            var mid = parameters.TryGetValue("mid", out var m) && int.TryParse(m, out var midY) ? midY : 0;
            var frame = parameters.TryGetValue("frame", out var f) ? f : "";
            var xhost = parameters.TryGetValue("xhost", out var xh) ? xh : "";
            var pre = parameters.TryGetValue("pre", out var pv) && pv == "1";
            var nonumber = parameters.TryGetValue("nonumber", out var nv) && nv == "1";
            var mutate = parameters.TryGetValue("mutate", out var mv) && mv == "1";
            // A clamping iframe reproduces the R5-3 case: the element is capped by
            // max-height, so writing only a height would leave content truncated.
            var clamp = parameters.TryGetValue("clamp", out var cv) && cv == "1";
            // R5-2 failure injection: the first scroll container expands normally, then the
            // second one throws once on its first style write, exercising the production
            // prepare failure + finally restore + retry path.
            var prepfail = parameters.TryGetValue("prepfail", out var pf) && pf == "1";
            // Parsed before the /official fixture branch, which substitutes it into the
            // official-DOM page script; keeping the declaration later made the file
            // uncompilable (CS0841) and blocked every downstream verification step.
            var mode = parameters.TryGetValue("mode", out var mo) ? mo : "ok";
            var mutateScript = mutate
                ? "setInterval(function () { if (window.__cccPrepareAt && !window.__cccMutated) { window.__cccMutated = true; setTimeout(function () { var t = document.getElementById('result'); if (t) t.innerHTML = '<tr><td>报关单号 310120260000009999 申报日期 2026-09-24 放行日期 2026-09-24 海关状态 已放行</td></tr>'; }, 100); } }, 50);"
                : "";

            if (path.StartsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return $$"""
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>frame</title>
                <style>html,body{margin:0;padding:0;background:#fff}#ftop{height:80px;background:#0088CC}
                .scroller{height:300px;overflow-y:auto;border:1px solid #ccc}
                .inner{position:relative;height:1200px;background:linear-gradient(#eef3f8,#dde6f0)}table{width:100%;border-collapse:collapse}
                td{padding:6px;font:13px system-ui;color:#0f1b2d}#fband{height:120px;background:#7700AA}
                #scrollband{position:absolute;left:0;right:0;top:1080px;height:120px;background:#CC00CC}</style></head><body>
                <div id="ftop"></div>
                {{FormHtml(decl, result, autoQuery, error, pre, nonumber, "")}}
                <div id="fband"></div>
                </body></html>
                """;
            }

            // A fixture that mirrors the official query frame DOM: no-placeholder #entryId
            // next to a 报关单号 label, #queryBtn, and a #queryDetail result rendered as
            // .display-content > .content-field > .field-order/.field-time/.field-text
            // (see planning/singlewindow-statistics.js). The top-level shell has no query.
            if (path.StartsWith("/official", StringComparison.OrdinalIgnoreCase))
            {
                return """
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>官方查询</title>
                <style>
                html,body{margin:0;padding:0;background:#fff;font:13px system-ui}
                #officialTop{height:80px;background:#0088CC}
                #officialBottom{height:120px;background:#7700AA}
                #queryForm{margin:8px;padding:8px;border:1px solid #eee}
                .content-area{min-height:240px}
                .content-field{border:1px solid #ddd;margin:6px;padding:6px;width:140px;word-break:break-all}
                .field-title{font-weight:600}
                .field-order{font-family:monospace}
                </style></head><body>
                <div id="officialTop"></div>
                <form id="queryForm">
                  <label for="entryId">报关单号</label>
                  <input type="text" id="entryId" name="entryId" maxlength="18">
                  <input type="text" id="randomcode" size="8">
                  <button type="button" id="queryBtn">查询</button>
                </form>
                <div class="content-area" id="queryDetail"></div>
                <div id="officialError"></div>
                <div id="officialBottom"></div>
                <script>
                  var no = "__DECL__";
                  var mode = "__MODE__";
                  document.getElementById('entryId').value = no;
                  function officialResult(number) {
                    var titles = ['放行','查验','报关','转关','转关运抵','舱单','理货','装运','到港','离港','卸货','申报'];
                    var html = '';
                    for (var i = 0; i < titles.length; i++) {
                      html += '<div id="node' + i + '" class="dec-cus"><div class="content-title"><div><span class="content-title-text">' + titles[i] + '</span></div></div>' +
                        '<div class="display-content">' +
                        '<div class="content-field"><div class="field-title">' + titles[i] + '</div><div class="field-order">' + number + '</div>' +
                        '<div class="field-time">2026-09-24 10:00</div><div class="field-text">已' + titles[i] + '</div></div>' +
                        '</div></div>';
                    }
                    return html;
                  }
                  document.getElementById('queryBtn').addEventListener('click', function () {
                    var detail = document.getElementById('queryDetail');
                    if (mode === 'empty') { detail.innerHTML = ''; return; }
                    if (mode === 'error') { document.getElementById('officialError').textContent = '温馨提示：验证码无效，请点击刷新验证码！'; return; }
                    var number = mode === 'wrong' ? '310120260000009999' : no;
                    detail.innerHTML = officialResult(number);
                  });
                  if (mode === 'mutate') {
                    setInterval(function () { if (window.__cccPrepareAt && !window.__cccMutated) { window.__cccMutated = true; setTimeout(function () { document.querySelectorAll('#queryDetail .field-order').forEach(function (e) { e.textContent = '310120260000009999'; }); }, 100); } }, 50);
                  }
                </script></body></html>
                """.Replace("__DECL__", decl).Replace("__MODE__", mode);
            }

            var midDiv = mid > 0 ? $"<div id=\"mid\" style=\"position:absolute;left:0;top:{mid}px;width:100%;height:200px;background:#00AA55\"></div>" : "";
            var isFrame = frame is "1" or "cross" or "official";
            var frameSrc = frame == "cross" && xhost.Length > 0
                ? xhost
                : frame == "official"
                    ? $"/official?result={result}&mode={mode}&decl={decl}"
                    : $"/frame?content=800&result={result}&query={(autoQuery ? "1" : "0")}";
            // A cross-origin frame scenario puts the query form and the matching result ONLY
            // inside the iframe: the top-level shell has neither query nor result, exactly
            // like the official page. Only the frame's own tall auxiliary content is used.
            var body = frame == "cross"
                ? $"""
                  <div id="top"></div>
                  <iframe id="inner" src="{frameSrc}" style="width:100%;height:400px;{(clamp ? "max-height:400px !important;" : "")}border:0"></iframe>
                  <div id="bottom"></div>
                  """
                : isFrame
                    ? $"""
                      <div id="top"></div>
                      <iframe id="inner" src="{frameSrc}" style="width:100%;height:400px;border:0"></iframe>
                      <div id="bottom"></div>
                      """
                    : $"""
                      <div id="top"></div>
                      <div id="content">{(prepfail ? "<div id=\"bad-scroller\" class=\"badscroller\"><div class=\"badinner\"></div></div>" : "")}{FormHtml(decl, result, autoQuery, error, pre, nonumber, mutateScript)}</div>
                      <div id="bottom"></div>
                      """;
            var contentRule = isFrame ? "height:auto;" : $"height:{content}px;";
            // One-shot injection: the bad container throws on its very first style write and
            // immediately removes the override, so the retry after the restored failure can
            // run the normal prepare path and save.
            var prepfailScript = prepfail ? """
            <script>
            (function () {
              var bad = document.getElementById('bad-scroller');
              if (!bad) return;
              Object.defineProperty(bad.style, 'setProperty', {
                configurable: true,
                value: function () {
                  delete bad.style.setProperty;
                  throw new Error('injected-prepare-failure');
                }
              });
              window.__cccPrepareFailArmed = bad.style.hasOwnProperty('setProperty');
            })();
            </script>
            """ : "";
            return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>受控核验页</title>
            <style>
              html,body{margin:0;padding:0;background:#ffffff;position:relative}
              #top{height:200px;background:#654321}
              #content{ {{contentRule}}box-sizing:border-box;padding:16px}
              .scroller{height:400px;overflow-y:auto;border:1px solid #cccccc;margin-top:12px}
              .scroller .inner{position:relative;height:1800px;background:linear-gradient(#eef3f8,#dde6f0)}
              .badscroller{height:400px;overflow-y:auto;border:1px solid #999999;margin-bottom:12px}
              .badscroller .badinner{position:relative;height:1800px;background:linear-gradient(#f8eee0,#f0dde0)}
              #scrollband{position:absolute;left:0;right:0;top:1680px;height:120px;background:#CC00CC}
              #bottom{height:200px;background:#123456}
              table{width:100%;border-collapse:collapse}td{padding:8px;font:14px system-ui;color:#0f1b2d}
              iframe{display:block}
            </style></head><body>
            {{midDiv}}
            {{body}}
            {{prepfailScript}}
            </body></html>
            """;
        }
    }
}
