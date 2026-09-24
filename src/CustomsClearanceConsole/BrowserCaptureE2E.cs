using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CustomsClearanceConsole;

/// <summary>
/// Controlled end-to-end harness for the in-page manual long screenshot. It serves
/// a synthetic single-window-like page over loopback, drives the injected card
/// button through the CDP binding, and asserts success, mismatch, too-long,
/// double-click, re-injection, tiling, missing query, captcha error, rejected write,
/// repeat capture and same-origin frame behaviour. It never touches the real website.
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

    private static async Task<Scenario> RunSuccessAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "success");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000001", folder,
            server.Url("/page?content=2400&result=310120260000000001"), browser, headless: true);
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
            var restore = await session.EvaluateRawAsync("JSON.stringify(window.__cccLastCapture||null)", CancellationToken.None);
            if (!restore.Contains("widgetWasHidden\":true", StringComparison.OrdinalIgnoreCase)) details.Add($"页面未记录控件隐藏/恢复：{restore}");
            var hosts = await session.CountWidgetHostsAsync(CancellationToken.None);
            if (hosts != 1) details.Add($"控件实例数 {hosts}，应为 1。");
            if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("保存后卡片按钮不可再次点击。");
        }
        return new Scenario("success-fullpage", details.Count == 0, details.Count == 0 ? ["整页首尾标记、控件隐藏与恢复均通过"] : details);
    }

    private static async Task<Scenario> RunTilingAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "tiling");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000011", folder,
            server.Url("/page?content=14000&result=310120260000000011&mid=11900"), browser, headless: true);
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
        }
        return new Scenario("tiling-beyond-viewport", details.Count == 0, details.Count == 0 ? ["跨 12000px 分片拼接且接缝内容完整"] : details);
    }

    private static async Task<Scenario> RunMismatchAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "mismatch");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000002", folder,
            server.Url("/page?content=1200&result=310120260000000099"), browser, headless: true);
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
            server.Url("/page?content=30000&result=310120260000000003"), browser, maxPixels: 300_000, headless: true);
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
            server.Url("/page?content=1500&result=310120260000000004"), browser, headless: true);
        var completion = new TaskCompletionSource<BrowserCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureCompleted += (_, value) => completion.TrySetResult(value);
        await session.StartAsync(CancellationToken.None);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None))
            return new Scenario("double-click-single-file", false, ["控件未注入。"]);
        var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!identity.StartsWith("ready|", StringComparison.Ordinal))
            return new Scenario("double-click-single-file", false, [$"查询结果未就绪：{identity}"]);
        await Task.WhenAll(session.ClickCaptureButtonAsync(CancellationToken.None), session.ClickCaptureButtonAsync(CancellationToken.None));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
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
            server.Url("/page?content=900&result=310120260000000005"), browser, headless: true);
        await session.StartAsync(CancellationToken.None);
        var details = new List<string>();
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) details.Add("初次注入失败。");
        try { await session.EvaluateRawAsync("location.reload()", CancellationToken.None); }
        catch (Exception ex) { AppLog.Write($"E2E 刷新页面时连接重置：{ex.Message}"); }
        await Task.Delay(1500);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None)) details.Add("刷新后未重新注入控件。");
        var hosts = await session.CountWidgetHostsAsync(CancellationToken.None);
        if (hosts != 1) details.Add($"刷新后控件实例数 {hosts}，应为 1。");
        return new Scenario("reload-reinject-single", details.Count == 0, details.Count == 0 ? ["刷新后单实例重新注入"] : details);
    }

    private static async Task<Scenario> RunNoQueryAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "no-query");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000006", folder,
            server.Url("/page?content=900&result=310120260000000006&query=0"), browser, headless: true);
        var result = await DriveAsync(session, null, waitForSettled: false);
        var details = new List<string>();
        if (result.State != "error") details.Add($"未查询应拒绝保存，实际状态 {result.State}：{result.Message}");
        if (Directory.EnumerateFiles(folder, "*.png").Any()) details.Add("未查询仍然生成了截图。");
        return new Scenario("no-query-rejected", details.Count == 0, details.Count == 0 ? ["未查询时未保存假成功"] : details);
    }

    private static async Task<Scenario> RunCaptchaErrorAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "captcha-error");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000007", folder,
            server.Url("/page?content=900&result=310120260000000007&error=%E9%AA%8C%E8%AF%81%E7%A0%81%E9%94%99%E8%AF%AF"), browser, headless: true);
        var result = await DriveAsync(session, null, waitForSettled: true);
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
            server.Url("/page?content=900&result=310120260000000008"), browser, headless: true);
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
            server.Url("/page?content=1200&result=310120260000000009"), browser, headless: true);
        var details = new List<string>();
        var first = await DriveAsync(session, null, waitForSettled: true);
        if (first.State != "saved") details.Add($"首次截图失败：{first.Message}");
        if (!await session.CanCaptureAsync(CancellationToken.None)) details.Add("首次截图后按钮不可用，无法重新截图。");
        var completion = new TaskCompletionSource<BrowserCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureCompleted += (_, value) => completion.TrySetResult(value);
        await session.ClickCaptureButtonAsync(CancellationToken.None);
        var second = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        if (second.State != "saved") details.Add($"重新截图失败：{second.Message}");
        var files = Directory.EnumerateFiles(folder, "*.png").ToList();
        if (files.Count != 2) details.Add($"重新截图应生成 2 个唯一文件，实际 {files.Count}。");
        return new Scenario("repeat-capture-visible-retry", details.Count == 0, details.Count == 0 ? ["可见按钮可再次截图且文件名唯一"] : details);
    }

    private static async Task<Scenario> RunIframeAsync(string outputFolder, string browser, TestServer server)
    {
        var folder = Path.Combine(outputFolder, "iframe");
        Directory.CreateDirectory(folder);
        await using var session = new BrowserValidation("310120260000000010", folder,
            server.Url("/page?content=300&result=310120260000000010&frame=1"), browser, headless: true);
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

    private static async Task<BrowserCaptureResult> DriveAsync(BrowserValidation session, string? evidencePath, bool waitForSettled)
    {
        var completion = new TaskCompletionSource<BrowserCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureCompleted += (_, value) => completion.TrySetResult(value);
        await session.StartAsync(CancellationToken.None);
        if (!await session.WaitForWidgetAsync(TimeSpan.FromSeconds(30), CancellationToken.None))
            return new BrowserCaptureResult(session.SessionId, session.DeclarationNo, "error", null, "控件未注入。");
        if (waitForSettled)
        {
            var identity = await session.WaitForIdentitySettledAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (identity.StartsWith("waiting|", StringComparison.Ordinal) || identity.Length == 0)
                return new BrowserCaptureResult(session.SessionId, session.DeclarationNo, "error", null, "查询结果未就绪。");
        }
        else
        {
            await Task.Delay(1200);
        }
        await session.ClickCaptureButtonAsync(CancellationToken.None);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        if (evidencePath is not null && result.FilePath is not null && File.Exists(result.FilePath))
            File.Copy(result.FilePath, evidencePath, overwrite: true);
        return result;
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

        private static string FormHtml(string decl, string result, bool autoQuery, string error)
        {
            var resultRow = error.Length > 0
                ? ""
                : $"<tr><td>报关单号 {result} 申报日期 2026-09-24 放行日期 2026-09-24 海关状态 已放行</td></tr>";
            var errorScript = autoQuery
                ? $"setTimeout(function () {{ document.getElementById('query').click(); document.getElementById('result').innerHTML = {JsonSerializer.Serialize(resultRow)}; }}, 400);"
                : "";
            var errorTextScript = error.Length > 0 ? $"document.getElementById('error').textContent = {JsonSerializer.Serialize(error)};" : "";
            return $$"""
              <form onsubmit="return false"><label>报关单号 <input data-customs-declaration value="{{decl}}"></label>
              <button type="button" id="query">查询</button></form>
              <div id="error"></div><div id="loading" class="loading" hidden>加载中</div>
              <div class="scroller"><div class="inner"></div></div>
              <table><tbody id="result"></tbody></table>
              <script>
                document.querySelector('input').value = {{JsonSerializer.Serialize(decl)}};
                {{errorScript}}
                {{errorTextScript}}
              </script>
            """;
        }

        private static string Page(string path)
        {
            var parameters = Parameters(path);
            var content = parameters.TryGetValue("content", out var c) && int.TryParse(c, out var height) ? height : 1200;
            var result = parameters.TryGetValue("result", out var r) ? r : "310120260000000001";
            var decl = parameters.TryGetValue("decl", out var d) ? d : result;
            var autoQuery = !parameters.TryGetValue("query", out var q) || q != "0";
            var error = parameters.TryGetValue("error", out var e) ? e : "";
            var mid = parameters.TryGetValue("mid", out var m) && int.TryParse(m, out var midY) ? midY : 0;
            var frame = parameters.TryGetValue("frame", out var f) && f == "1";

            if (path.StartsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return $$"""
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>frame</title>
                <style>html,body{margin:0;padding:0;background:#fff}.scroller{height:300px;overflow-y:auto;border:1px solid #ccc}
                .inner{height:1200px;background:linear-gradient(#eef3f8,#dde6f0)}table{width:100%;border-collapse:collapse}
                td{padding:6px;font:13px system-ui;color:#0f1b2d}#fband{height:120px;background:#7700AA}</style></head><body>
                {{FormHtml(decl, result, autoQuery, error)}}
                <div id="fband"></div>
                </body></html>
                """;
            }

            var midDiv = mid > 0 ? $"<div id=\"mid\" style=\"position:absolute;left:0;top:{mid}px;width:100%;height:200px;background:#00AA55\"></div>" : "";
            var body = frame
                ? $"""
                  <div id="top"></div>
                  <iframe id="inner" src="/frame?content=800&result={result}&query={(autoQuery ? "1" : "0")}" style="width:100%;height:400px;border:0"></iframe>
                  <div id="bottom"></div>
                  """
                : $"""
                  <div id="top"></div>
                  <div id="content">{FormHtml(decl, result, autoQuery, error)}</div>
                  <div id="bottom"></div>
                  """;
            var contentRule = frame ? "height:auto;" : $"height:{content}px;";
            return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>受控核验页</title>
            <style>
              html,body{margin:0;padding:0;background:#ffffff;position:relative}
              #top{height:200px;background:#654321}
              #content{ {{contentRule}}box-sizing:border-box;padding:16px}
              .scroller{height:400px;overflow-y:auto;border:1px solid #cccccc;margin-top:12px}
              .scroller .inner{height:1800px;background:linear-gradient(#eef3f8,#dde6f0)}
              #bottom{height:200px;background:#123456}
              table{width:100%;border-collapse:collapse}td{padding:8px;font:14px system-ui;color:#0f1b2d}
              iframe{display:block}
            </style></head><body>
            {{midDiv}}
            {{body}}
            </body></html>
            """;
        }
    }
}
