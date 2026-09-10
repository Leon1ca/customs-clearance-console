using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CustomsClearanceConsole;

internal sealed class BrowserValidation : IAsyncDisposable
{
    public const string PrimaryUrl = "https://www.singlewindow.cn/#/publicInquiryDetail?id=pi4";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private DevToolsClient? _devTools;
    private Process? _process;
    private int _debugPort;

    internal sealed record BrowserChoice(string Path, string DisplayName, bool UsedFallback);

    public static string? FindBrowser(string preference)
    {
        var edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        var chrome = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        var preferred = preference.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ? chrome : edge;
        var fallback = preference.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ? edge : chrome;
        return preferred.Concat(fallback).FirstOrDefault(File.Exists);
    }

    public static BrowserChoice ResolveBrowser()
    {
        var defaultPath = GetDefaultBrowserExecutable();
        if (!string.IsNullOrWhiteSpace(defaultPath) && File.Exists(defaultPath))
        {
            var fileName = Path.GetFileName(defaultPath);
            if (fileName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
                return new BrowserChoice(defaultPath, "系统默认浏览器（Edge）", false);
            if (fileName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
                return new BrowserChoice(defaultPath, "系统默认浏览器（Chrome）", false);
        }

        var fallback = FindBrowser("Edge") ?? throw new FileNotFoundException("系统默认浏览器不支持自动填写，且未找到 Microsoft Edge 或 Google Chrome。请至少安装其中一种浏览器。");
        var name = Path.GetFileName(fallback).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome";
        return new BrowserChoice(fallback, name, true);
    }

    private static string? GetDefaultBrowserExecutable()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId)) return null;
            using var commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }
            var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd >= 0 ? command[..(exeEnd + 4)].Trim() : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取系统默认浏览器失败：{ex.Message}");
            return null;
        }
    }

    public Task<string> StartAsync(string declarationNo, CancellationToken cancellationToken) =>
        StartCoreAsync(declarationNo, ResolveBrowser(), cancellationToken);


    private async Task<string> StartCoreAsync(string declarationNo, BrowserChoice choice, CancellationToken cancellationToken)
    {
        var browser = choice.Path;
        var port = GetFreePort(); _debugPort = port;
        var profile = Path.Combine(AppLog.Folder, "BrowserProfiles", $"{Path.GetFileNameWithoutExtension(browser)}-{port}");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo
        {
            FileName = browser,
            UseShellExecute = true
        };
        start.ArgumentList.Add($"--remote-debugging-port={port}");
        start.ArgumentList.Add("--remote-allow-origins=*");
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--start-maximized");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(PrimaryUrl);
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器。");

        var websocket = await WaitForPageAsync(port, cancellationToken);
        await ConnectDevToolsAsync(websocket, cancellationToken);
        var monitorInstall = BuildResultMonitorInstallScript(declarationNo);
        await _devTools!.CommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = monitorInstall }, cancellationToken);
        await EvaluateInAllFramesAsync(monitorInstall, cancellationToken);

        var filled = false;
        for (var i = 0; i < 30 && !filled; i++)
        {
            await Task.Delay(500, cancellationToken);
            string result;
            try { result = await EvaluateInAllFramesAsync(BuildAutofillScript(declarationNo), cancellationToken); }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                try { await ReconnectAsync(cancellationToken); } catch { return "核验网站已打开，但自动填写连接中断。请手动输入报关单号并查询；若自动长截图不可用，请使用浏览器的网页捕获功能，截图名保存为报关单号。"; }
                result = "";
            }
            filled = result.Contains("filled", StringComparison.OrdinalIgnoreCase);
        }
        var browserNote = choice.UsedFallback
            ? $"系统默认浏览器不支持自动填写，已自动使用 {choice.DisplayName}。"
            : $"已使用{choice.DisplayName}。";
        return filled
            ? $"{browserNote} 已在正文的“报关单号”栏填入单号。请在浏览器中输入验证码并点击查询，程序将自动检测结果并保存长截图。"
            : $"{browserNote} 核验网站已打开，但网页结构可能已更新。请手动输入报关单号并查询；程序仍会尝试自动检测结果并截图。";
    }

    private async Task ConnectDevToolsAsync(string websocket, CancellationToken token)
    {
        if (_devTools is not null) await _devTools.DisposeAsync();
        _devTools = await DevToolsClient.ConnectAsync(websocket, token);
        await _devTools.CommandAsync("Page.enable", null, token);
        await _devTools.CommandAsync("Runtime.enable", null, token);
    }

    private async Task ReconnectAsync(CancellationToken token)
    {
        var websocket = await WaitForPageAsync(_debugPort, token);
        await ConnectDevToolsAsync(websocket, token);
    }

    public async Task<string> CaptureLongScreenshotAsync(string declarationNo, string targetFolder, CancellationToken cancellationToken, bool manualConfirmed = false)
    {
        try
        {
            return await CaptureLongScreenshotCoreAsync(declarationNo, targetFolder, cancellationToken, manualConfirmed);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await ReconnectAsync(cancellationToken);
            return await CaptureLongScreenshotCoreAsync(declarationNo, targetFolder, cancellationToken, manualConfirmed);
        }
    }

    public async Task<bool> WaitForStableResultAsync(
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        string lastFingerprint = "";
        string lastStage = "";
        var stableHits = 0;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string probe;
            try
            {
                probe = await EvaluateInAllFramesAsync(BuildResultProbeScript(), cancellationToken,
                    "ready|", "loading|", "error|", "mismatch|");
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                await ReconnectAsync(cancellationToken);
                probe = "query|reconnected";
            }

            var separator = probe.IndexOf('|');
            var stage = separator < 0 ? probe : probe[..separator];
            var fingerprint = separator < 0 ? "" : probe[(separator + 1)..];
            if (!string.Equals(stage, lastStage, StringComparison.Ordinal))
            {
                progress?.Report(stage switch
                {
                    "query" => "已检测到网页查询，正在等待结果……",
                    "loading" => "网页正在加载查询结果……",
                    "ready" => "已检测到查询结果，正在确认页面内容稳定……",
                    "mismatch" => "网页中的报关单号与本次核验不一致，请恢复正确单号并重新查询。",
                    "error" => "网页提示验证码或查询条件有误，请重新输入后再次查询。",
                    _ => "等待在网页中输入验证码并点击“查询”……"
                });
                lastStage = stage;
            }

            if (stage == "ready")
            {
                stableHits = fingerprint == lastFingerprint ? stableHits + 1 : 0;
                lastFingerprint = fingerprint;
                if (stableHits >= 3) return true;
            }
            else
            {
                stableHits = 0;
                lastFingerprint = "";
            }
            await Task.Delay(400, cancellationToken);
        }
        progress?.Report("90 秒内未自动确认查询结果，可检查网页后点击“立即截图”。");
        return false;
    }

    private async Task<string> CaptureLongScreenshotCoreAsync(string declarationNo, string targetFolder, CancellationToken cancellationToken, bool manualConfirmed)
    {
        if (_devTools is null) throw new InvalidOperationException("浏览器控制连接已断开，请重新打开核验。");
        var identity = await EvaluateInAllFramesAsync(BuildResultProbeScript(), cancellationToken, "ready|", "mismatch|");
        if (identity.StartsWith("mismatch|", StringComparison.Ordinal)) throw new InvalidOperationException("网页单号不一致，请恢复本次报关单号并重新查询。");
        if (!manualConfirmed && !identity.StartsWith("ready|", StringComparison.Ordinal)) throw new InvalidOperationException("结果尚未确认，请检查网页后手动留存。");
        Directory.CreateDirectory(targetFolder);

        await EvaluateAsync(@"(() => {
          const all = [...document.querySelectorAll('*')];
          for (const el of all) {
            const s = getComputedStyle(el);
            if ((s.overflowY === 'auto' || s.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 80) {
              el.style.setProperty('height', el.scrollHeight + 'px', 'important');
              el.style.setProperty('max-height', 'none', 'important');
              el.style.setProperty('overflow-y', 'visible', 'important');
            }
          }
          window.scrollTo(0, 0); return 'expanded';
        })()", cancellationToken);
        await Task.Delay(400, cancellationToken);

        var metrics = await _devTools.CommandAsync("Page.getLayoutMetrics", null, cancellationToken);
        var content = metrics.GetProperty("result").GetProperty("contentSize");
        var width = Math.Clamp((int)Math.Ceiling(content.GetProperty("width").GetDouble()), 800, 5000);
        var height = Math.Clamp((int)Math.Ceiling(content.GetProperty("height").GetDouble()), 600, 60000);
        var file = GetAvailableScreenshotPath(targetFolder, declarationNo);

        const int tileHeight = 12000;
        if ((long)width * height > 60_000_000) throw new InvalidOperationException("网页过长，请使用浏览器的网页捕获功能分段保存。");
        using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(full))
        {
            graphics.Clear(Color.White);
            for (var y = 0; y < height; y += tileHeight)
            {
                var h = Math.Min(tileHeight, height - y);
                var response = await _devTools.CommandAsync("Page.captureScreenshot", new
                {
                    format = "png", fromSurface = true, captureBeyondViewport = true,
                    clip = new { x = 0, y, width, height = h, scale = 1 }
                }, cancellationToken);
                var data = Convert.FromBase64String(response.GetProperty("result").GetProperty("data").GetString()!);
                using var stream = new MemoryStream(data);
                using var tile = new Bitmap(stream);
                graphics.DrawImageUnscaled(tile, 0, y);
            }
        }
        full.Save(file, ImageFormat.Png);
        return file;
    }

    private static string GetAvailableScreenshotPath(string folder, string declarationNo)
    {
        var path = Path.Combine(folder, declarationNo + ".png");
        if (!File.Exists(path)) return path;
        return Path.Combine(folder, $"{declarationNo}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
    }

    private async Task<string> EvaluateAsync(string expression, CancellationToken token)
    {
        var response = await _devTools!.CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, token);
        try { return response.GetProperty("result").GetProperty("result").GetProperty("value").ToString(); }
        catch { return ""; }
    }

    private async Task<string> EvaluateInAllFramesAsync(string expression, CancellationToken token, params string[] acceptedMarkers)
    {
        if (_devTools is null) return "";
        if (acceptedMarkers.Length == 0) acceptedMarkers = ["filled"];
        static bool Accepted(string value, IReadOnlyCollection<string> markers) =>
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var main = await EvaluateAsync(expression, token);
        if (Accepted(main, acceptedMarkers)) return main;

        JsonElement frameTree;
        try { frameTree = await _devTools.CommandAsync("Page.getFrameTree", null, token); }
        catch { return main; }

        foreach (var frameId in EnumerateFrameIds(frameTree.GetProperty("result").GetProperty("frameTree")))
        {
            try
            {
                var world = await _devTools.CommandAsync("Page.createIsolatedWorld", new
                {
                    frameId,
                    worldName = "customs-console-autofill",
                    grantUniveralAccess = true
                }, token);
                var contextId = world.GetProperty("result").GetProperty("executionContextId").GetInt32();
                var response = await _devTools.CommandAsync("Runtime.evaluate", new
                {
                    expression,
                    contextId,
                    returnByValue = true,
                    awaitPromise = true
                }, token);
                var value = response.GetProperty("result").GetProperty("result").TryGetProperty("value", out var resultValue)
                    ? resultValue.ToString() : "";
                if (Accepted(value, acceptedMarkers)) return value;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { }
        }
        return main;
    }

    private static IEnumerable<string> EnumerateFrameIds(JsonElement tree)
    {
        if (tree.TryGetProperty("frame", out var frame) && frame.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } frameId)
            yield return frameId;
        if (!tree.TryGetProperty("childFrames", out var children) || children.ValueKind != JsonValueKind.Array) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var childId in EnumerateFrameIds(child)) yield return childId;
    }

    private async Task<string> WaitForPageAsync(int port, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var i = 0; i < 40; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await _http.GetStringAsync($"http://127.0.0.1:{port}/json", cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var target = doc.RootElement.EnumerateArray().FirstOrDefault(x =>
                    x.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                    x.TryGetProperty("url", out var url) && (url.GetString()?.Contains("singlewindow", StringComparison.OrdinalIgnoreCase) ?? false) &&
                    x.TryGetProperty("webSocketDebuggerUrl", out _));
                if (target.ValueKind == JsonValueKind.Undefined)
                    target = doc.RootElement.EnumerateArray().FirstOrDefault(x =>
                        x.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                        x.TryGetProperty("webSocketDebuggerUrl", out _));
                if (target.ValueKind != JsonValueKind.Undefined)
                    return target.GetProperty("webSocketDebuggerUrl").GetString()!;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException("浏览器已打开，但无法建立自动填写连接。", last);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        if (exception is WebSocketException or SocketException or IOException or HttpRequestException or ObjectDisposedException)
            return true;
        return exception.InnerException is not null && IsConnectionFailure(exception.InnerException);
    }

    private static string BuildAutofillScript(string declarationNo) => BrowserScript("autofill", declarationNo);
    private static string BuildResultMonitorInstallScript(string declarationNo) => BrowserScript("monitor", declarationNo);
    private static string BuildResultProbeScript() => BrowserScript("probe");
    private static string BrowserScript(string name, string? declarationNo = null)
    {
        using var stream = typeof(BrowserValidation).Assembly.GetManifestResourceStream($"CustomsClearanceConsole.BrowserScripts.{name}.js")
            ?? throw new InvalidOperationException("缺少网页核验脚本。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__DECLARATION_NO__", JsonSerializer.Serialize(declarationNo));
    }

    public async ValueTask DisposeAsync()
    {
        var devTools = Interlocked.Exchange(ref _devTools, null);
        try
        {
            if (devTools is not null) await devTools.DisposeAsync();
        }
        catch (Exception ex) when (IsConnectionFailure(ex)) { }
        finally { _http.Dispose(); }
    }

    private sealed class DevToolsClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _nextId;

        public static async Task<DevToolsClient> ConnectAsync(string url, CancellationToken token)
        {
            var client = new DevToolsClient();
            await client._socket.ConnectAsync(new Uri(url), token);
            return client;
        }

        public async Task<JsonElement> CommandAsync(string method, object? parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
            while (true)
            {
                var receiveBuffer = new byte[65536];
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("浏览器控制连接已关闭。");
                    stream.Write(receiveBuffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(stream.ToArray());
                if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    if (document.RootElement.TryGetProperty("error", out var error))
                        throw new InvalidOperationException(error.ToString());
                    return document.RootElement.Clone();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            // Browsers may close the DevTools socket first during navigation or window shutdown.
            // Abort performs local cleanup only and never tries to write a closing frame to a dead peer.
            try { _socket.Abort(); } catch { }
            try { _socket.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
