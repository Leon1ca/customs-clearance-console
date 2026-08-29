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
    public const string FallbackUrl = "https://swapp.singlewindow.cn/qspserver/sw/qsp/query/view/export?ngBasePath=https://swapp.singlewindow.cn:443/qspserver/";
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

    public async Task<string> StartAsync(string declarationNo, string browserPreference, CancellationToken cancellationToken)
    {
        return await StartCoreAsync(declarationNo, ResolveBrowser(), cancellationToken);
    }

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
            ? $"{browserNote} 已在正文的“报关单号”栏填入单号。请在浏览器中输入验证码并点击查询，确认信息完整后回到本窗口截图。"
            : $"{browserNote} 核验网站已打开，但网页结构可能已更新。请手动输入报关单号并查询，完成人工验证后回到本窗口截图。";
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

    public async Task NavigateFallbackAsync(string declarationNo, CancellationToken cancellationToken)
    {
        try
        {
            await NavigateFallbackCoreAsync(declarationNo, cancellationToken);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await ReconnectAsync(cancellationToken);
            await NavigateFallbackCoreAsync(declarationNo, cancellationToken);
        }
    }

    public async Task<string> CaptureLongScreenshotAsync(string declarationNo, string targetFolder, CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureLongScreenshotCoreAsync(declarationNo, targetFolder, cancellationToken);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await ReconnectAsync(cancellationToken);
            return await CaptureLongScreenshotCoreAsync(declarationNo, targetFolder, cancellationToken);
        }
    }

    private async Task NavigateFallbackCoreAsync(string declarationNo, CancellationToken cancellationToken)
    {
        if (_devTools is null) throw new InvalidOperationException("浏览器控制连接已断开，请重新打开核验。");
        await _devTools.CommandAsync("Page.navigate", new { url = FallbackUrl }, cancellationToken);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(500, cancellationToken);
            var result = await EvaluateInAllFramesAsync(BuildAutofillScript(declarationNo), cancellationToken);
            if (result.Contains("filled", StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private async Task<string> CaptureLongScreenshotCoreAsync(string declarationNo, string targetFolder, CancellationToken cancellationToken)
    {
        if (_devTools is null) throw new InvalidOperationException("浏览器控制连接已断开，请重新打开核验。");
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
        var images = new List<Bitmap>();
        try
        {
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
                images.Add(new Bitmap(stream));
            }
            using var full = new Bitmap(width, images.Sum(x => x.Height), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(full))
            {
                graphics.Clear(Color.White);
                var y = 0;
                foreach (var image in images) { graphics.DrawImageUnscaled(image, 0, y); y += image.Height; }
            }
            full.Save(file, ImageFormat.Png);
        }
        finally { foreach (var image in images) image.Dispose(); }
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

    private async Task<string> EvaluateInAllFramesAsync(string expression, CancellationToken token)
    {
        if (_devTools is null) return "";
        var main = await EvaluateAsync(expression, token);
        if (main.Contains("filled", StringComparison.OrdinalIgnoreCase)) return main;

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
                if (value.Contains("filled", StringComparison.OrdinalIgnoreCase)) return value;
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

    private static string BuildAutofillScript(string declarationNo)
    {
        var encoded = JsonSerializer.Serialize(declarationNo);
        return $@"(() => {{
          const no = {encoded};
          const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const normalize = s => (s || '').replace(/[\s：:＊*]/g, '');
          const roots = [document];
          for (let n = 0; n < roots.length; n++)
            for (const e of roots[n].querySelectorAll('*')) if (e.shadowRoot && !roots.includes(e.shadowRoot)) roots.push(e.shadowRoot);
          const queryAll = selector => roots.flatMap(root => [...root.querySelectorAll(selector)]);
          const labels = queryAll('label,span,td,div')
            .filter(e => visible(e) && e.children.length <= 3 && normalize(e.innerText) === '报关单号');
          const inputs = queryAll('input')
            .filter(i => visible(i) && (!i.type || ['text','tel'].includes(i.type)));
          const distance = (label, input) => {{
            const a = label.getBoundingClientRect(), b = input.getBoundingClientRect();
            const vertical = Math.abs((a.top + a.bottom) / 2 - (b.top + b.bottom) / 2);
            const horizontal = b.left >= a.right ? b.left - a.right : Math.abs(b.left - a.left) + 300;
            return vertical * 5 + horizontal;
          }};
          let target = null;
          target = inputs.find(i => /报关单号|declaration|customs/i.test([i.placeholder,i.name,i.id,i.getAttribute('aria-label')].filter(Boolean).join(' '))) || null;
          for (const label of labels) {{
            if (target && visible(target)) break;
            if (label.htmlFor) target = queryAll('#' + CSS.escape(label.htmlFor))[0] || null;
            if (!target) {{
              const row = label.closest('tr,.form-group,.el-form-item,.ant-form-item,.layui-form-item') || label.parentElement;
              target = row?.querySelector('input:not([type=hidden])') || null;
            }}
            if (target && visible(target)) break;
          }}
          if ((!target || !visible(target)) && labels.length && inputs.length)
            target = inputs.slice().sort((a,b) => distance(labels[0], a) - distance(labels[0], b))[0];
          if (!target) return 'waiting';
          target.focus();
          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
          if (setter) setter.call(target, no); else target.value = no;
          target.setAttribute('value', no);
          for (const type of ['input','change','keyup','blur']) target.dispatchEvent(new Event(type, {{bubbles:true, composed:true}}));
          target.focus();
          return target.value === no ? 'filled' : 'waiting';
        }})()";
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
