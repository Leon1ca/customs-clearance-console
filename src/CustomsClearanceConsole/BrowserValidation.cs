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
    public const string PrimaryUrl = TargetUrlPolicy.PrimaryUrl;
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
    private readonly bool _allowTestTarget;
    private readonly Uri? _testOrigin;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private readonly Dictionary<string, string> _frameUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _defaultContexts = [];
    private CdpClient? _cdp;
    private Process? _process;
    private int _debugPort;
    private int _captureGate;
    private int _navigationGeneration;
    private string? _shellScriptIdentifier;
    private string _mainFrameId = "";
    private string _currentUrl = "";
    private bool _disposed;

    public BrowserValidation(string declarationNo, string targetFolder, string? urlOverride = null,
        string? browserPathOverride = null, long maxPixels = DefaultMaxPixels, bool headless = false,
        bool allowTestTarget = false)
    {
        DeclarationNo = declarationNo;
        _targetFolder = targetFolder;
        _urlOverride = urlOverride;
        _browserPathOverride = browserPathOverride;
        _maxPixels = maxPixels > 0 ? maxPixels : DefaultMaxPixels;
        _headless = headless;
        _allowTestTarget = allowTestTarget;
        if (!string.IsNullOrWhiteSpace(urlOverride))
        {
            if (!allowTestTarget)
                throw new ArgumentException("非生产目标地址只能在显式测试选项中授权。", nameof(urlOverride));
            if (!Uri.TryCreate(urlOverride, UriKind.Absolute, out var testUri) || testUri.Host.Length == 0)
                throw new ArgumentException("测试目标地址必须是绝对 URL。", nameof(urlOverride));
            _testOrigin = new Uri($"{testUri.Scheme}://{testUri.Host}:{testUri.Port}");
        }
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

    private string TargetFragment => _allowTestTarget ? _testOrigin?.ToString() ?? "测试目标" : "singlewindow.cn";

    /// <summary>Parses a URL and matches its exact scheme/host/port against the authorized target.</summary>
    private bool MatchesTargetOrigin(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        uri = parsed;
        if (_allowTestTarget)
            return _testOrigin is not null &&
                   parsed.Scheme.Equals(_testOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   parsed.Host.Equals(_testOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
                   parsed.Port == _testOrigin.Port;
        if (!parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        return parsed.Host.Equals("www.singlewindow.cn", StringComparison.OrdinalIgnoreCase) ||
               parsed.Host.Equals("singlewindow.cn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The main page must be the exact single-window inquiry route; a URL merely containing
    /// "singlewindow" (for example https://example.com/?singlewindow) is not accepted.
    /// </summary>
    private bool IsTargetUrl(string? url)
    {
        if (!MatchesTargetOrigin(url, out _)) return false;
        return _allowTestTarget || TargetUrlPolicy.IsSingleWindowInquiry(url);
    }

    /// <summary>Sub-frames only have to belong to the authorized origin; the card lives in the main frame.</summary>
    private bool IsTargetFrameUrl(string? url, string frameId) =>
        frameId.Equals(_mainFrameId, StringComparison.Ordinal) ? IsTargetUrl(url) : MatchesTargetOrigin(url, out _);

    /// <summary>
    /// A web request is only honored when its execution context belongs to an allowed frame
    /// of the target origin, not merely when some known context exists.
    /// </summary>
    private bool IsAuthorizedContext(int contextId)
    {
        string? frameId = null;
        lock (_defaultContexts) _defaultContexts.TryGetValue(contextId, out frameId);
        if (string.IsNullOrEmpty(frameId)) return IsTargetUrl(_currentUrl);
        string? frameUrl = null;
        lock (_frameUrls) _frameUrls.TryGetValue(frameId, out frameUrl);
        if (string.IsNullOrWhiteSpace(frameUrl))
            frameUrl = frameId.Equals(_mainFrameId, StringComparison.Ordinal) ? _currentUrl : null;
        return !string.IsNullOrWhiteSpace(frameUrl) && IsTargetFrameUrl(frameUrl, frameId);
    }

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
        Interlocked.Exchange(ref _navigationGeneration, 0);
        _shellScriptIdentifier = null;
        _cdp = await CdpClient.ConnectAsync(websocket, token);
        _cdp.Closed += OnConnectionClosed;
        _cdp.On("Runtime.executionContextCreated", OnContextCreated);
        _cdp.On("Runtime.executionContextDestroyed", OnContextDestroyed);
        _cdp.On("Runtime.executionContextsCleared", OnContextsCleared);
        _cdp.On("Runtime.bindingCalled", OnBindingCalled);
        _cdp.On("Page.frameNavigated", OnFrameNavigated);
        await _cdp.SendAsync("Page.enable", null, token);
        await _cdp.SendAsync("Runtime.enable", null, token);
        await _cdp.SendAsync("DOM.enable", null, token);
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
        await RegisterShellScriptAsync(token);
    }

    private async Task RegisterShellScriptAsync(CancellationToken token)
    {
        if (_cdp is null) return;
        // Never accumulate duplicate per-document scripts across reconnects.
        if (_shellScriptIdentifier is not null)
        {
            try { await _cdp.SendAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier = _shellScriptIdentifier }, token); }
            catch (Exception ex) { AppLog.Write($"移除旧网页脚本失败：{ex.Message}"); }
            _shellScriptIdentifier = null;
        }
        try
        {
            var response = await _cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new { source = ShellScript() }, token);
            _shellScriptIdentifier = response.GetProperty("result").TryGetProperty("identifier", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) { AppLog.Write($"注册网页脚本失败：{ex.Message}"); }
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

    /// <summary>A navigated-away frame must not leave a stale context id in the active set.</summary>
    private void OnContextDestroyed(JsonElement message)
    {
        try
        {
            var id = message.GetProperty("params").GetProperty("executionContextId").GetInt32();
            lock (_defaultContexts) _defaultContexts.Remove(id);
        }
        catch (Exception ex) { AppLog.Write($"清理执行上下文失败：{ex.Message}"); }
    }

    private void OnContextsCleared(JsonElement message)
    {
        lock (_defaultContexts) _defaultContexts.Clear();
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
            if (isMain)
            {
                Interlocked.Increment(ref _navigationGeneration);
                if (!IsTargetUrl(url) && url.Length > 0)
                    _ = SetWidgetStateAsync("error", "已离开核验目标页面，截图请求将被忽略。");
            }
        }
        catch (Exception ex) { AppLog.Write($"读取页面导航失败：{ex.Message}"); }
    }

    private async Task ReconnectAsync(CancellationToken token)
    {
        // Serialize concurrent recovery attempts (socket close handler and E2E helper).
        await _reconnectGate.WaitAsync(token);
        try
        {
            var websocket = await WaitForPageAsync(_debugPort, token);
            await ConnectAsync(websocket, token);
            await InjectShellAsync(token);
        }
        finally { _reconnectGate.Release(); }
    }

    private async Task InjectShellAsync(CancellationToken token)
    {
        if (_cdp is null) return;
        await _cdp.SendAsync("Runtime.addBinding", new { name = "cccRequestCapture" }, token);
        await _cdp.SendAsync("Runtime.addBinding", new { name = "cccOpenFolder" }, token);
        // Frames that finished loading before this connect never ran the new-document
        // script, so the monitor is installed into every existing allowed frame. The
        // card itself belongs to the main frame only.
        try { await EvaluateAllContextsAsync(MonitorScript(), token, childrenFirst: false, bestEffort: true); }
        catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"向子框架安装监视器失败：{ex.Message}"); }
        try { await EvaluateContextAsync(ShellScript(), null, token, awaitPromise: true); }
        catch (Exception ex) when (IsConnectionFailure(ex)) { }
    }

    private string MonitorScript() => BrowserScript("monitor", DeclarationNo);

    private string ShellScript()
    {
        var monitor = MonitorScript();
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
            if (!IsAuthorizedContext(contextId))
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

        var generation = Volatile.Read(ref _navigationGeneration);

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

        // A layout-independent fingerprint of the result region: it is re-checked before
        // the atomic save so a query change, a result re-render or a navigation during
        // the capture aborts the save instead of backfilling a stale image.
        var stableBefore = await VerifyStableAsync(token);
        if (stableBefore.StartsWith("mismatch|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "mismatch", null, "网页结果单号与本次核验不一致，未保存截图。");
        if (stableBefore.StartsWith("error|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "网页提示验证码或查询条件有误，未保存截图。");
        if (!stableBefore.StartsWith("stable|", StringComparison.Ordinal))
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "查询结果尚未确认，未保存截图。请先在网页完成查询。");
        await Task.Delay(350, token);
        var stableSecond = await VerifyStableAsync(token);
        if (stableSecond != stableBefore)
            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "查询结果仍在变化，未保存截图。请稍后重试。");

        Directory.CreateDirectory(_targetFolder);
        var preparedContexts = new List<int?>();
        var expandedFrames = new List<string>();
        try
        {
            await PrepareContextsAsync(BrowserScript("capture-prepare"), preparedContexts, token);
            await Task.Delay(300, token);

            // Cross-origin frames cannot be resized from page JS. The CDP frame owner is
            // grown to the frame document height so the complete frame content is painted;
            // a frame that cannot be expanded rejects the capture instead of truncating it.
            var failedFrames = await ExpandFramesAsync(expandedFrames, token);
            if (failedFrames.Count > 0)
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null,
                    $"存在无法完整捕获的网页框架（{string.Join("、", failedFrames)}），未保存截图。");

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
                    if (Volatile.Read(ref _navigationGeneration) != generation || !IsTargetUrl(_currentUrl))
                        return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "捕获期间页面已导航，未保存截图。");
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
            // Final identity/generation re-validation before the atomic move.
            if (Volatile.Read(ref _navigationGeneration) != generation || !IsTargetUrl(_currentUrl))
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "捕获期间页面已导航，未保存截图。");
            var stableAfter = await VerifyStableAsync(token);
            if (!stableAfter.StartsWith("stable|", StringComparison.Ordinal) || stableAfter != stableBefore)
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, "捕获期间查询结果发生变化，未保存截图。");
            var file = SaveAtomically(full);
            return new BrowserCaptureResult(SessionId, DeclarationNo, "saved", file, "长截图已保存。");
        }
        finally
        {
            // Restoration must run even when the capture token was cancelled. Frame heights
            // are restored through CDP first (cross-origin included), then the page-level
            // script puts back scroll offsets, overflow and widget visibility.
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await RestoreFramesAsync(expandedFrames, restore.Token);
            await RestoreContextsAsync(BrowserScript("capture-restore"), preparedContexts, restore.Token);
        }
    }

    /// <summary>Layout-independent result fingerprint used to detect in-flight changes.</summary>
    private Task<string> VerifyStableAsync(CancellationToken token) =>
        EvaluateAllContextsAsync(BrowserScript("verify", DeclarationNo), token, false,
            "stable|", "mismatch|", "error|", "loading|", "waiting|", "stale|");

    private static bool IsPreparedMarker(string value) =>
        value.Contains("prepared:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("already-prepared", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the prepare expression context by context and records exactly which contexts
    /// were modified, so a mid-way failure still restores every already-prepared frame.
    /// </summary>
    private async Task PrepareContextsAsync(string expression, List<int?> prepared, CancellationToken token)
    {
        if (_cdp is null) return;
        List<KeyValuePair<int, string>> contexts;
        lock (_defaultContexts) contexts = _defaultContexts.ToList();
        foreach (var context in contexts.OrderBy(x => x.Value.Equals(_mainFrameId, StringComparison.Ordinal) ? 1 : 0))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var value = await EvaluateContextAsync(expression, context.Key, token, awaitPromise: true);
                if (IsPreparedMarker(value)) prepared.Add(context.Key);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"子框架状态准备失败：{ex.Message}"); }
        }
        if (token.IsCancellationRequested) return;
        try
        {
            var main = await EvaluateContextAsync(expression, null, token, awaitPromise: true);
            if (IsPreparedMarker(main)) prepared.Add(null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"主框架状态准备失败：{ex.Message}"); }
    }

    private async Task RestoreContextsAsync(string expression, IReadOnlyList<int?> prepared, CancellationToken token)
    {
        if (_cdp is null) return;
        foreach (var contextId in prepared)
        {
            if (token.IsCancellationRequested) break;
            try { await EvaluateContextAsync(expression, contextId, token, awaitPromise: true); }
            catch (Exception ex) { AppLog.Write($"网页状态恢复失败：{ex.Message}"); }
        }
    }

    private static void CollectFrames(JsonElement tree, List<(string FrameId, string Url)> frames)
    {
        if (!tree.TryGetProperty("frame", out var frame)) return;
        var id = frame.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
        var url = frame.TryGetProperty("url", out var urlValue) ? urlValue.GetString() ?? "" : "";
        if (id.Length > 0) frames.Add((id, url));
        if (tree.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) CollectFrames(child, frames);
    }

    private async Task<string?> GetFrameOwnerObjectIdAsync(string frameId, CancellationToken token)
    {
        if (_cdp is null) return null;
        var owner = await _cdp.SendAsync("DOM.getFrameOwner", new { frameId }, token);
        if (!owner.GetProperty("result").TryGetProperty("backendNodeId", out var backend) || backend.GetInt32() <= 0) return null;
        var resolved = await _cdp.SendAsync("DOM.resolveNode", new { backendNodeId = backend.GetInt32() }, token);
        if (!resolved.GetProperty("result").TryGetProperty("object", out var remote)) return null;
        return remote.TryGetProperty("objectId", out var objectId) ? objectId.GetString() : null;
    }

    private async Task<string> CallFunctionOnAsync(string objectId, string function, object? argument, CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        object parameters = argument is null
            ? (object)new { objectId, functionDeclaration = function, returnByValue = true, awaitPromise = false }
            : new { objectId, functionDeclaration = function, arguments = new[] { argument }, returnByValue = true, awaitPromise = false };
        var response = await cdp.SendAsync("Runtime.callFunctionOn", parameters, token);
        try
        {
            var result = response.GetProperty("result").GetProperty("result");
            return result.TryGetProperty("value", out var value) ? value.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Grows every authorized sub-frame's owner element to the frame document height using
    /// CDP (so cross-origin frames are included). Returns the frames that could not be
    /// expanded while their content is taller than the element.
    /// </summary>
    private async Task<List<string>> ExpandFramesAsync(List<string> expanded, CancellationToken token)
    {
        var failed = new List<string>();
        if (_cdp is null) return failed;
        JsonElement tree;
        try { tree = await _cdp.SendAsync("Page.getFrameTree", null, token); }
        catch (Exception ex) { AppLog.Write($"读取框架树失败：{ex.Message}"); return failed; }
        var frames = new List<(string FrameId, string Url)>();
        CollectFrames(tree.GetProperty("result").GetProperty("frameTree"), frames);
        foreach (var (frameId, url) in frames)
        {
            if (frameId.Equals(_mainFrameId, StringComparison.Ordinal)) continue;
            token.ThrowIfCancellationRequested();
            try
            {
                int? contextId = null;
                lock (_defaultContexts)
                {
                    var match = _defaultContexts.FirstOrDefault(x => x.Value.Equals(frameId, StringComparison.Ordinal));
                    if (match.Value is not null) contextId = match.Key;
                }
                if (contextId is null && _cdp is not null)
                {
                    // Out-of-process / already-loaded frames may not report a default
                    // context; an isolated world still exposes the frame DOM through CDP.
                    try
                    {
                        var world = await _cdp.SendAsync("Page.createIsolatedWorld", new
                        {
                            frameId,
                            worldName = "customs-console-frame-expand",
                            grantUniveralAccess = true
                        }, token);
                        contextId = world.GetProperty("result").GetProperty("executionContextId").GetInt32();
                    }
                    catch (Exception ex) { AppLog.Write($"无法建立框架执行环境 {frameId}：{ex.Message}"); }
                }
                if (contextId is null)
                {
                    failed.Add(frameId);
                    continue;
                }
                var heightText = await EvaluateContextAsync(
                    "String(Math.max(document.documentElement ? document.documentElement.scrollHeight : 0, document.body ? document.body.scrollHeight : 0))",
                    contextId, token, awaitPromise: false);
                if (!int.TryParse(heightText.Trim(), out var frameHeight) || frameHeight <= 0) continue;
                var objectId = await GetFrameOwnerObjectIdAsync(frameId, token);
                if (string.IsNullOrEmpty(objectId))
                {
                    failed.Add(frameId);
                    continue;
                }
                var currentText = await CallFunctionOnAsync(objectId, "function(){ return String(this && this.tagName==='IFRAME' ? (this.clientHeight||0) : 0); }", null, token);
                if (!int.TryParse(currentText.Trim(), out var currentHeight)) currentHeight = 0;
                if (frameHeight <= currentHeight + 2) continue;
                var applied = await CallFunctionOnAsync(objectId,
                    "function(h){ if(!this || this.tagName!=='IFRAME') return '0'; if(this.__cccPrevHeight===undefined){ this.__cccPrevHeight = this.style.getPropertyValue('height') || ''; } var target=Math.max(this.clientHeight||0, h); this.style.setProperty('height', target+'px','important'); return String(target); }",
                    new { value = frameHeight }, token);
                if (!int.TryParse(applied.Trim(), out var target) || target < frameHeight)
                {
                    failed.Add(frameId);
                    continue;
                }
                expanded.Add(frameId);
            }
            catch (Exception ex) when (!IsConnectionFailure(ex))
            {
                AppLog.Write($"扩展框架 {frameId} 失败：{ex.Message}");
                failed.Add(frameId);
            }
        }
        return failed;
    }

    private async Task RestoreFramesAsync(IReadOnlyList<string> frameIds, CancellationToken token)
    {
        if (_cdp is null) return;
        foreach (var frameId in frameIds)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var objectId = await GetFrameOwnerObjectIdAsync(frameId, token);
                if (string.IsNullOrEmpty(objectId)) continue;
                await CallFunctionOnAsync(objectId,
                    "function(){ if(this && this.tagName==='IFRAME' && this.__cccPrevHeight!==undefined){ if(this.__cccPrevHeight) this.style.setProperty('height', this.__cccPrevHeight, ''); else this.style.removeProperty('height'); delete this.__cccPrevHeight; } return 'ok'; }",
                    null, token);
            }
            catch (Exception ex) { AppLog.Write($"恢复框架高度失败：{ex.Message}"); }
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

    /// <summary>Hit-test result of the last real card-button click (E2E evidence).</summary>
    public string LastClickHitTarget { get; private set; } = "";

    /// <summary>
    /// Clicks the visible web card button with real CDP mouse input (not the internal
    /// requestCapture API), so the E2E exercises the same hit-tested path a user does.
    /// Returns false when the button rectangle cannot be located.
    /// </summary>
    public async Task<bool> ClickCaptureButtonAsync(CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        var rectJson = await EvaluateContextAsync(
            "JSON.stringify((window.__cccWidget && window.__cccWidget.captureButtonRect && window.__cccWidget.captureButtonRect()) || null)",
            null, token, awaitPromise: false);
        if (string.IsNullOrWhiteSpace(rectJson) || rectJson.TrimStart().StartsWith("null", StringComparison.OrdinalIgnoreCase)) return false;
        double x, y;
        try
        {
            using var document = JsonDocument.Parse(rectJson);
            var root = document.RootElement;
            x = root.GetProperty("x").GetDouble() + root.GetProperty("width").GetDouble() / 2;
            y = root.GetProperty("y").GetDouble() + root.GetProperty("height").GetDouble() / 2;
        }
        catch (Exception ex) { AppLog.Write($"读取卡片按钮位置失败：{ex.Message}"); return false; }
        // Confirm the point hits the card host before the trusted click.
        var point = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x},{y}");
        LastClickHitTarget = await EvaluateContextAsync(
            $"(() => {{ const el = document.elementFromPoint({point}); return el && (el.id === 'ccc-widget-host' || (el.closest && el.closest('#ccc-widget-host'))) ? 'host' : (el ? el.tagName : 'none'); }})()",
            null, token, awaitPromise: false);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none", clickCount = 0 }, token);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, token);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, token);
        return true;
    }

    /// <summary>Navigates the page (E2E helper for leave/return and reload flows).</summary>
    public async Task NavigateAsync(string url, CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        await cdp.SendAsync("Page.navigate", new { url }, token);
    }

    /// <summary>Aborts the control socket so the reconnect production path can be exercised.</summary>
    public void SimulateConnectionDropForTest() => _cdp?.AbortForTest();

    public Task<string> CurrentUrlAsync(CancellationToken token) =>
        EvaluateContextAsync("location.href", null, token, awaitPromise: false);

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

    /// <summary>Test hook: aborts the socket so the reconnect path can be exercised.</summary>
    internal void AbortForTest() => _socket.Abort();

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
