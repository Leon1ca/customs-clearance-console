using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CustomsClearanceConsole;

internal sealed record BrowserCaptureResult(string SessionId, string DeclarationNo, string State, string? FilePath, string Message);

/// <summary>
/// Controls a dedicated Edge/Chrome instance over the DevTools protocol and injects
/// the in-page capture card. Capture is requested by the user clicking the web card
/// (Runtime binding), never automatically. Only the configured target page may ask
/// for a capture or open a folder.
/// </summary>
internal sealed class BrowserValidation : IAsyncDisposable
{
    public const string PrimaryUrl = "https://www.singlewindow.cn/#/publicInquiryDetail?id=pi4";
    private const long DefaultMaxPixels = 60_000_000L;
    private const int TileHeight = 12_000;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DeclarationNo { get; }
    public bool IsRunning => _cdp is { IsClosed: false };

    public event EventHandler<BrowserCaptureResult>? CaptureCompleted;
    public event EventHandler<string>? StatusChanged;

    private readonly string _targetFolder;
    private readonly string? _urlOverride;
    private readonly string? _browserPathOverride;
    private readonly long _maxPixels;
    private readonly bool _headless;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string> _frameUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _defaultContexts = [];
    private CdpClient? _cdp;
    private Process? _process;
    private int _debugPort;
    private int _captureGate;
    private string _mainFrameId = "";
    private string _currentUrl = "";
    private bool _disposed;

    public BrowserValidation(string declarationNo, string targetFolder, string? urlOverride = null,
        string? browserPathOverride = null, long maxPixels = DefaultMaxPixels, bool headless = false)
    {
        DeclarationNo = declarationNo;
        _targetFolder = targetFolder;
        _urlOverride = urlOverride;
        _browserPathOverride = browserPathOverride;
        _maxPixels = maxPixels > 0 ? maxPixels : DefaultMaxPixels;
        _headless = headless;
    }

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

    private string TargetFragment => string.IsNullOrWhiteSpace(_urlOverride)
        ? "singlewindow"
        : new Uri(_urlOverride).Authority;

    private bool IsTargetUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Contains(TargetFragment, StringComparison.OrdinalIgnoreCase);

    public async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        BrowserChoice choice;
        if (!string.IsNullOrWhiteSpace(_browserPathOverride) && File.Exists(_browserPathOverride))
            choice = new BrowserChoice(_browserPathOverride, Path.GetFileNameWithoutExtension(_browserPathOverride), true);
        else
            choice = ResolveBrowser();

        var url = string.IsNullOrWhiteSpace(_urlOverride) ? PrimaryUrl : _urlOverride;
        _debugPort = GetFreePort();
        var profile = Path.Combine(AppLog.Folder, "BrowserProfiles", SessionId);
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo { FileName = choice.Path, UseShellExecute = false };
        start.ArgumentList.Add($"--remote-debugging-port={_debugPort}");
        start.ArgumentList.Add("--remote-allow-origins=*");
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-features=Translate");
        if (_headless)
        {
            start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add("--disable-gpu");
            start.ArgumentList.Add("--window-size=1280,900");
        }
        else
        {
            start.ArgumentList.Add("--start-maximized");
        }
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(url);
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器。");

        var websocket = await WaitForPageAsync(_debugPort, cancellationToken);
        await ConnectAsync(websocket, cancellationToken);
        await InjectShellAsync(cancellationToken);

        var filled = await AutofillAsync(cancellationToken);
        var browserNote = choice.UsedFallback
            ? $"默认浏览器不支持自动填写，已使用 {choice.DisplayName}。"
            : $"已使用{choice.DisplayName}。";
        return filled
            ? $"{browserNote} 已填入报关单号。请在网页输入验证码并点击查询，然后在网页右下角点击“长截图”。"
            : $"{browserNote} 核验页面已打开，请手动输入报关单号；结果出现后在网页右下角点击“长截图”。";
    }

    private async Task ConnectAsync(string websocket, CancellationToken token)
    {
        if (_cdp is not null) await _cdp.DisposeAsync();
        lock (_frameUrls) { _frameUrls.Clear(); _currentUrl = ""; _mainFrameId = ""; }
        lock (_defaultContexts) { _defaultContexts.Clear(); }
        _cdp = await CdpClient.ConnectAsync(websocket, token);
        _cdp.Closed += OnConnectionClosed;
        _cdp.On("Runtime.executionContextCreated", OnContextCreated);
        _cdp.On("Runtime.bindingCalled", OnBindingCalled);
        _cdp.On("Page.frameNavigated", OnFrameNavigated);
        await _cdp.SendAsync("Page.enable", null, token);
        await _cdp.SendAsync("Runtime.enable", null, token);
        // Seed the current URL/frame so binding validation works before the first
        // navigation event arrives.
        try
        {
            var tree = await _cdp.SendAsync("Page.getFrameTree", null, token);
            var root = tree.GetProperty("result").GetProperty("frameTree").GetProperty("frame");
            _mainFrameId = root.GetProperty("id").GetString() ?? "";
            _currentUrl = root.TryGetProperty("url", out var current) ? current.GetString() ?? "" : "";
        }
        catch (Exception ex) { AppLog.Write($"读取初始页面地址失败：{ex.Message}"); }
        await _cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = ShellScript() }, token);
    }

    private void OnConnectionClosed(string reason)
    {
        if (_disposed) return;
        StatusChanged?.Invoke(this, "浏览器连接中断，正在尝试恢复……");
        // Keep an explicit recovery task running so the status message is truthful.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 8 && !_disposed; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    await ReconnectAsync(timeout.Token);
                    StatusChanged?.Invoke(this, "浏览器连接已恢复。");
                    return;
                }
                catch (Exception ex) { AppLog.Write($"浏览器重连第 {attempt + 1} 次失败：{ex.Message}"); }
                try { await Task.Delay(1000); } catch { return; }
            }
        });
    }

    private void OnContextCreated(JsonElement message)
    {
        try
        {
            var context = message.GetProperty("params").GetProperty("context");
            if (!context.TryGetProperty("auxData", out var aux)) return;
            if (!aux.TryGetProperty("isDefault", out var isDefault) || !isDefault.GetBoolean()) return;
            var id = context.GetProperty("id").GetInt32();
            var frameId = aux.TryGetProperty("frameId", out var frame) ? frame.GetString() ?? "" : "";
            lock (_defaultContexts) { _defaultContexts[id] = frameId; }
        }
        catch (Exception ex) { AppLog.Write($"读取执行上下文失败：{ex.Message}"); }
    }

    private void OnFrameNavigated(JsonElement message)
    {
        try
        {
            var frame = message.GetProperty("params").GetProperty("frame");
            var frameId = frame.GetProperty("id").GetString() ?? "";
            var url = frame.TryGetProperty("url", out var value) ? value.GetString() ?? "" : "";
            var isMain = !frame.TryGetProperty("parentId", out _);
            lock (_frameUrls)
            {
                if (frameId.Length > 0) _frameUrls[frameId] = url;
                if (isMain) { _mainFrameId = frameId; _currentUrl = url; }
            }
            if (isMain && !IsTargetUrl(url) && url.Length > 0)
                _ = SetWidgetStateAsync("error", "已离开核验目标页面，截图请求将被忽略。");
        }
        catch (Exception ex) { AppLog.Write($"读取页面导航失败：{ex.Message}"); }
    }

    private async Task ReconnectAsync(CancellationToken token)
    {
        var websocket = await WaitForPageAsync(_debugPort, token);
        await ConnectAsync(websocket, token);
        await InjectShellAsync(token);
    }

    private async Task InjectShellAsync(CancellationToken token)
    {
        if (_cdp is null) return;
        await _cdp.SendAsync("Runtime.addBinding", new { name = "cccRequestCapture" }, token);
        await _cdp.SendAsync("Runtime.addBinding", new { name = "cccOpenFolder" }, token);
        try { await EvaluateContextAsync(ShellScript(), null, token, awaitPromise: true); }
        catch (Exception ex) when (IsConnectionFailure(ex)) { }
    }

    private string ShellScript()
    {
        var monitor = BrowserScript("monitor", DeclarationNo);
        var widget = BrowserScript("widget", DeclarationNo).Replace("__SAVE_DIR__", JsonSerializer.Serialize(_targetFolder));
        return monitor + ";\n" + widget + ";";
    }

    private async Task<bool> AutofillAsync(CancellationToken token)
    {
        var filled = false;
        for (var i = 0; i < 30 && !filled; i++)
        {
            await Task.Delay(500, token);
            string result;
            try { result = await EvaluateInAllFramesAsync(BrowserScript("autofill", DeclarationNo), token, "filled"); }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                try { await ReconnectAsync(token); } catch { return false; }
                result = "";
            }
            filled = result.Contains("filled", StringComparison.OrdinalIgnoreCase);
        }
        return filled;
    }

    // ---- capture ----

    private void OnBindingCalled(JsonElement message)
    {
        try
        {
            var parameters = message.GetProperty("params");
            var name = parameters.GetProperty("name").GetString();
            var contextId = parameters.TryGetProperty("executionContextId", out var context) ? context.GetInt32() : -1;
            var knownContext = false;
            lock (_defaultContexts) { knownContext = _defaultContexts.ContainsKey(contextId); }
            var targetUrl = IsTargetUrl(_currentUrl);
            if (!knownContext || !targetUrl)
            {
                AppLog.Write($"已忽略来源页面的网页请求：{name} · context={contextId} · url={_currentUrl}");
                _ = SetWidgetStateAsync("error", "当前页面不是核验目标页，已忽略网页请求。");
                return;
            }
            if (name == "cccRequestCapture")
            {
                if (Interlocked.CompareExchange(ref _captureGate, 1, 0) != 0) return;
                _ = Task.Run(async () =>
                {
                    try { await CaptureAndReportAsync(); }
                    finally { Interlocked.Exchange(ref _captureGate, 0); }
                });
            }
            else if (name == "cccOpenFolder")
            {
                // The page can only ask to open the already-configured folder.
                OpenTargetFolder();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"处理网页请求失败：{ex.Message}");
        }
    }

    private void OpenTargetFolder()
    {
        try
        {
            if (Directory.Exists(_targetFolder))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_targetFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write(ex); }
    }

    private async Task CaptureAndReportAsync()
    {
        BrowserCaptureResult result;
        try
        {
            result = await CaptureWithRetryAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            result = new BrowserCaptureResult(SessionId, DeclarationNo, "cancelled", null, "截图已取消。");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            result = new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, $"截图未保存：{ex.Message}");
        }
        await ReportAsync(result);
        CaptureCompleted?.Invoke(this, result);
    }

    private async Task<BrowserCaptureResult> CaptureWithRetryAsync(CancellationToken token)
    {
        try
        {
            return await CaptureCoreAsync(token);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await ReconnectAsync(token);
            return await CaptureCoreAsync(token);
        }
    }

    private async Task<BrowserCaptureResult> CaptureCoreAsync(CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开，请重新打开核验。");
        if (!IsTargetUrl(_currentUrl))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "当前页面不是核验目标页，未保存截图。");

        var identity = await ProbeIdentityAsync(token);
        if (identity.StartsWith("mismatch|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "mismatch", null, "网页结果单号与本次核验不一致，未保存截图。");
        if (identity.StartsWith("error|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "网页提示验证码或查询条件有误，未保存截图。");
        if (identity.StartsWith("loading|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "网页仍在加载查询结果，未保存截图。");
        if (identity.StartsWith("stale|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "查询结果尚未变化，未保存截图。请重新查询后再试。");
        if (!identity.StartsWith("ready|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "查询结果尚未确认，未保存截图。请先在网页完成查询。");

        // Require the result fingerprint to stay stable, so an in-flight render is
        // never captured mid-update.
        await Task.Delay(350, token);
        var second = await ProbeIdentityAsync(token);
        if (!second.StartsWith("ready|", StringComparison.Ordinal) || second != identity)
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "查询结果仍在变化，未保存截图。请稍后重试。");

        Directory.CreateDirectory(_targetFolder);
        var prepared = false;
        try
        {
            await EvaluateAllContextsAsync(BrowserScript("capture-prepare"), token, childrenFirst: true, bestEffort: false);
            prepared = true;
            await Task.Delay(350, token);

            var metrics = await cdp.SendAsync("Page.getLayoutMetrics", null, token);
            var content = metrics.GetProperty("result").GetProperty("contentSize");
            var width = (int)Math.Ceiling(content.GetProperty("width").GetDouble());
            var height = (int)Math.Ceiling(content.GetProperty("height").GetDouble());
            if (width <= 0 || height <= 0)
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "无法读取页面尺寸，未保存截图。");
            // Explicitly reject oversized pages; never shrink the clip to fake success.
            if ((long)width * height > _maxPixels || width > 16_384 || height > 65_535)
                return new BrowserCaptureResult(SessionId, DeclarationNo, "too-long", null, "页面超过 6000 万像素，未保存截图；请使用浏览器分段保存。");

            var tiles = (int)Math.Ceiling(height / (double)TileHeight);
            await ReportProgressAsync(0, tiles, "正在截取整页");
            using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(full))
            {
                graphics.Clear(Color.White);
                for (var index = 0; index < tiles; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var y = index * TileHeight;
                    var tileHeight = Math.Min(TileHeight, height - y);
                    var response = await cdp.SendAsync("Page.captureScreenshot", new
                    {
                        format = "png",
                        fromSurface = true,
                        captureBeyondViewport = true,
                        clip = new { x = 0, y, width, height = tileHeight, scale = 1 }
                    }, token);
                    var data = Convert.FromBase64String(response.GetProperty("result").GetProperty("data").GetString()!);
                    using var stream = new MemoryStream(data);
                    using var tile = new Bitmap(stream);
                    graphics.DrawImageUnscaled(tile, 0, y);
                    await ReportProgressAsync(index + 1, tiles, null);
                }
            }
            var file = SaveAtomically(full);
            return new BrowserCaptureResult(SessionId, DeclarationNo, "saved", file, "长截图已保存。");
        }
        finally
        {
            if (prepared)
            {
                // Restoration must run even when the capture token was cancelled.
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await EvaluateAllContextsAsync(BrowserScript("capture-restore"), restore.Token, childrenFirst: true, bestEffort: true);
            }
        }
    }

    private string SaveAtomically(Bitmap image)
    {
        Directory.CreateDirectory(_targetFolder);
        var finalPath = GetAvailableScreenshotPath(_targetFolder, DeclarationNo);
        var temporary = Path.Combine(_targetFolder, $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Save(temporary, ImageFormat.Png);
            File.Move(temporary, finalPath, overwrite: false);
            return finalPath;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string GetAvailableScreenshotPath(string folder, string declarationNo)
    {
        var path = Path.Combine(folder, declarationNo + ".png");
        if (!File.Exists(path)) return path;
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(folder, $"{declarationNo}_{DateTime.Now:yyyyMMdd_HHmmss}_{index:D2}.png");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{declarationNo}_{Guid.NewGuid():N}.png");
    }

    private async Task SetWidgetStateAsync(string state, string message)
    {
        try
        {
            if (_cdp is null || _cdp.IsClosed) return;
            var json = JsonSerializer.Serialize(new { message });
            await EvaluateContextAsync($"window.__cccWidget && window.__cccWidget.setState({JsonSerializer.Serialize(state)}, {json});", null, CancellationToken.None, awaitPromise: false);
        }
        catch (Exception ex) { AppLog.Write($"更新网页卡片状态失败：{ex.Message}"); }
    }

    private async Task ReportAsync(BrowserCaptureResult result)
    {
        try
        {
            if (_cdp is null || _cdp.IsClosed) return;
            var json = JsonSerializer.Serialize(new { file = result.FilePath is null ? "" : Path.GetFileName(result.FilePath), message = result.Message });
            await EvaluateContextAsync($"window.__cccWidget && window.__cccWidget.setState({JsonSerializer.Serialize(result.State)}, {json});", null, _lifetime.Token, awaitPromise: false);
        }
        catch (Exception ex) { AppLog.Write($"更新网页卡片状态失败：{ex.Message}"); }
    }

    private async Task ReportProgressAsync(int done, int total, string? text)
    {
        try
        {
            if (_cdp is null || _cdp.IsClosed) return;
            var label = text is null ? "" : JsonSerializer.Serialize(text);
            await EvaluateContextAsync($"window.__cccWidget && window.__cccWidget.setProgress({done}, {total}, {label});", null, _lifetime.Token, awaitPromise: false);
        }
        catch (Exception ex) { AppLog.Write($"更新网页进度失败：{ex.Message}"); }
    }

    // ---- E2E helpers ----

    public async Task<bool> WaitForWidgetAsync(TimeSpan timeout, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var value = await EvaluateContextAsync("!!(window.__cccWidget && window.__cccWidget.declarationNo === " + JsonSerializer.Serialize(DeclarationNo) + ")", null, token, awaitPromise: false);
                if (value.Contains("true", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (IsConnectionFailure(ex)) { try { await ReconnectAsync(token); } catch { } }
            await Task.Delay(150, token);
        }
        return false;
    }

    public Task<string> ProbeIdentityAsync(CancellationToken token) =>
        EvaluateAllContextsAsync(BrowserScript("identity", DeclarationNo), token, false, "ready|", "mismatch|", "error|", "loading|", "stale|");

    public async Task<string> WaitForIdentitySettledAsync(TimeSpan timeout, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        string last = "";
        while (DateTime.UtcNow - started < timeout)
        {
            token.ThrowIfCancellationRequested();
            last = await ProbeIdentityAsync(token);
            if (last.Length > 0 && !last.StartsWith("waiting|", StringComparison.Ordinal)) return last;
            await Task.Delay(150, token);
        }
        return last;
    }

    /// <summary>Drives the web card button exactly as a user click would (E2E helper).</summary>
    public async Task ClickCaptureButtonAsync(CancellationToken token)
    {
        await EvaluateContextAsync("window.__cccWidget && window.__cccWidget.requestCapture()", null, token, awaitPromise: false);
    }

    public Task<bool> CanCaptureAsync(CancellationToken token) =>
        EvaluateBooleanAsync("!!(window.__cccWidget && window.__cccWidget.canCapture())", token);

    public Task<int> CountWidgetHostsAsync(CancellationToken token) =>
        EvaluateIntAsync("document.querySelectorAll('#ccc-widget-host').length", token);

    public Task<string> EvaluateRawAsync(string expression, CancellationToken token) =>
        EvaluateContextAsync(expression, null, token, awaitPromise: false);

    private async Task<bool> EvaluateBooleanAsync(string expression, CancellationToken token)
    {
        var value = await EvaluateContextAsync(expression, null, token, awaitPromise: false);
        return value.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> EvaluateIntAsync(string expression, CancellationToken token)
    {
        var value = await EvaluateContextAsync(expression, null, token, awaitPromise: false);
        return int.TryParse(value.Trim(), out var result) ? result : -1;
    }

    // ---- CDP evaluation helpers ----

    private async Task<string> EvaluateContextAsync(string expression, int? contextId, CancellationToken token, bool awaitPromise)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        var parameters = contextId is null
            ? (object)new { expression, returnByValue = true, awaitPromise }
            : new { expression, contextId = contextId.Value, returnByValue = true, awaitPromise };
        var response = await cdp.SendAsync("Runtime.evaluate", parameters, token);
        try
        {
            var result = response.GetProperty("result").GetProperty("result");
            return result.TryGetProperty("value", out var value) ? value.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Evaluates an expression in every default (page-world) execution context so
    /// page state such as the query monitor is visible, unlike isolated worlds.
    /// </summary>
    private async Task<string> EvaluateAllContextsAsync(string expression, CancellationToken token, bool childrenFirst, params string[] acceptedMarkers)
    {
        if (_cdp is null) return "";
        if (acceptedMarkers.Length == 0) acceptedMarkers = ["ready|", "mismatch|", "error|", "loading|", "stale|", "prepared:", "restored:", "nothing-to-restore"];
        static bool Accepted(string value, IReadOnlyCollection<string> markers) =>
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var main = await EvaluateContextAsync(expression, null, token, awaitPromise: true);
        if (Accepted(main, acceptedMarkers)) return main;

        List<KeyValuePair<int, string>> contexts;
        lock (_defaultContexts) contexts = _defaultContexts.ToList();
        var ordered = childrenFirst
            ? contexts.OrderBy(x => string.Equals(x.Value, _mainFrameId, StringComparison.Ordinal) ? 1 : 0).ToList()
            : contexts.OrderBy(x => string.Equals(x.Value, _mainFrameId, StringComparison.Ordinal) ? 0 : 1).ToList();
        foreach (var context in ordered)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var value = await EvaluateContextAsync(expression, context.Key, token, awaitPromise: true);
                if (Accepted(value, acceptedMarkers)) return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { }
        }
        return main;
    }

    private async Task EvaluateAllContextsAsync(string expression, CancellationToken token, bool childrenFirst, bool bestEffort)
    {
        if (_cdp is null) return;
        List<KeyValuePair<int, string>> contexts;
        lock (_defaultContexts) contexts = _defaultContexts.ToList();
        var ordered = childrenFirst
            ? contexts.OrderBy(x => string.Equals(x.Value, _mainFrameId, StringComparison.Ordinal) ? 1 : 0).ToList()
            : contexts.OrderBy(x => string.Equals(x.Value, _mainFrameId, StringComparison.Ordinal) ? 0 : 1).ToList();
        foreach (var context in ordered)
        {
            if (token.IsCancellationRequested) break;
            try { await EvaluateContextAsync(expression, context.Key, token, awaitPromise: true); }
            catch (Exception ex) { if (!bestEffort) throw; AppLog.Write($"网页状态准备/恢复失败：{ex.Message}"); }
        }
        try { await EvaluateContextAsync(expression, null, token, awaitPromise: true); }
        catch (Exception ex) { if (!bestEffort) throw; AppLog.Write($"网页状态准备/恢复失败：{ex.Message}"); }
    }

    private async Task<string> EvaluateInAllFramesAsync(string expression, CancellationToken token, params string[] acceptedMarkers)
    {
        if (_cdp is null) return "";
        if (acceptedMarkers.Length == 0) acceptedMarkers = ["filled"];
        static bool Accepted(string value, IReadOnlyCollection<string> markers) =>
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var main = await EvaluateContextAsync(expression, null, token, awaitPromise: true);
        if (Accepted(main, acceptedMarkers)) return main;

        JsonElement frameTree;
        try { frameTree = await _cdp.SendAsync("Page.getFrameTree", null, token); }
        catch { return main; }

        foreach (var frameId in EnumerateFrameIds(frameTree.GetProperty("result").GetProperty("frameTree")))
        {
            try
            {
                var world = await _cdp.SendAsync("Page.createIsolatedWorld", new
                {
                    frameId,
                    worldName = "customs-console-autofill",
                    grantUniveralAccess = true
                }, token);
                var contextId = world.GetProperty("result").GetProperty("executionContextId").GetInt32();
                var value = await EvaluateContextAsync(expression, contextId, token, awaitPromise: true);
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
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 80; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", cancellationToken);
                using var doc = JsonDocument.Parse(json);
                // Only the configured target page may be attached; never fall back to
                // an unrelated tab such as a startup page.
                var target = doc.RootElement.EnumerateArray().FirstOrDefault(x =>
                    x.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                    x.TryGetProperty("url", out var url) && IsTargetUrl(url.GetString()) &&
                    x.TryGetProperty("webSocketDebuggerUrl", out _));
                if (target.ValueKind != JsonValueKind.Undefined)
                    return target.GetProperty("webSocketDebuggerUrl").GetString()!;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException($"浏览器已打开，但未找到目标页面（{TargetFragment}）的调试连接。", last);
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

    private static string BrowserScript(string name, string? declarationNo = null)
    {
        using var stream = typeof(BrowserValidation).Assembly.GetManifestResourceStream($"CustomsClearanceConsole.BrowserScripts.{name}.js")
            ?? throw new InvalidOperationException($"缺少网页脚本：{name}.js");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("__DECLARATION_NO__", JsonSerializer.Serialize(declarationNo));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetime.Cancel(); } catch { }
        var cdp = Interlocked.Exchange(ref _cdp, null);
        if (cdp is not null)
        {
            try { await cdp.SendAsync("Browser.close", null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
            await cdp.DisposeAsync();
        }
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>
/// Minimal DevTools protocol client with a single background receive loop. Commands
/// are serialized and correlated by id; events are dispatched by method name, so a
/// command never consumes another command's response or drops an event.
/// </summary>
internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _events = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _readerLifetime = new();
    private int _nextId;
    private Task? _reader;
    private volatile bool _closed;

    public event Action<string>? Closed;
    public bool IsClosed => _closed;

    public static async Task<CdpClient> ConnectAsync(string url, CancellationToken token)
    {
        var client = new CdpClient();
        await client._socket.ConnectAsync(new Uri(url), token);
        client._reader = Task.Run(client.ReceiveLoopAsync);
        return client;
    }

    public void On(string method, Action<JsonElement> handler) => _events[method] = handler;

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken token)
    {
        if (_closed) throw new IOException("浏览器控制连接已关闭。");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        try
        {
            await _sendLock.WaitAsync(token);
            try { await _socket.SendAsync(payload, WebSocketMessageType.Text, true, token); }
            finally { _sendLock.Release(); }
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (!_readerLifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _readerLifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { FailAll(new IOException("浏览器控制连接已关闭。")); return; }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (message.Length == 0) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(message.ToArray()); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt32(out var id))
                    {
                        if (!_pending.TryRemove(id, out var completion)) continue;
                        if (root.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.ToString()));
                        else completion.TrySetResult(root.Clone());
                    }
                    else if (root.TryGetProperty("method", out var methodProperty))
                    {
                        var method = methodProperty.GetString();
                        if (method is not null && _events.TryGetValue(method, out var handler))
                        {
                            try { handler(root.Clone()); }
                            catch (Exception ex) { AppLog.Write($"CDP 事件处理失败：{method} · {ex.Message}"); }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FailAll(ex);
        }
    }

    private void FailAll(Exception exception)
    {
        if (_closed) return;
        _closed = true;
        foreach (var pair in _pending) pair.Value.TrySetException(exception);
        _pending.Clear();
        Closed?.Invoke(exception.Message);
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        try { _readerLifetime.Cancel(); } catch { }
        try { _socket.Abort(); } catch { }
        try { _socket.Dispose(); } catch { }
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _readerLifetime.Dispose();
        _sendLock.Dispose();
    }
}
