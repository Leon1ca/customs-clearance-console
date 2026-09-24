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
    /// <summary>Raised once when the dedicated browser process exits (for example the user closed it).</summary>
    public event EventHandler? Ended;

    private readonly string _targetFolder;
    private readonly string? _urlOverride;
    private readonly string? _browserPathOverride;
    private readonly long _maxPixels;
    private readonly bool _headless;
    private readonly bool _allowTestTarget;
    private readonly Uri? _testOrigin;
    private readonly IReadOnlyList<string>? _extraBrowserArgs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    // A default execution context is only unique per CDP session: the page session and a
    // flattened OOPIF session both start numbering at 1. Contexts are therefore keyed by
    // (sessionId, contextId) and stored as records (R5-4 / OOPIF).
    private sealed record FrameContext(int ContextId, string FrameId, string? SessionId);

    private readonly Dictionary<string, string> _frameUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameParents = new(StringComparer.Ordinal);
    private readonly List<FrameContext> _frameContexts = [];
    private readonly Dictionary<string, string> _frameTargetSessions = new(StringComparer.Ordinal);
    private CdpClient? _cdp;
    private Process? _process;
    private int _debugPort;
    private int _captureGate;
    private int _navigationGeneration;
    private int _viewportReadFailures;
    // Baseline viewports for the bounded render-readiness gate: the card click must wait for
    // the capture's temporary viewport enlargement to be restored, and the production
    // completion must not be published before that restore is observable in the page.
    private (int Width, int Height, double Scale)? _clickBaselineViewport;
    private (int Width, int Height, double Scale)? _captureBaselineViewport;
    private string? _shellScriptIdentifier;
    private string? _profileFolder;
    private int _endedRaised;
    private string _mainFrameId = "";
    private string _currentUrl = "";
    private volatile bool _disposed;

    public BrowserValidation(string declarationNo, string targetFolder, string? urlOverride = null,
        string? browserPathOverride = null, long maxPixels = DefaultMaxPixels, bool headless = false,
        bool allowTestTarget = false, IReadOnlyList<string>? extraBrowserArgs = null)
    {
        DeclarationNo = declarationNo;
        _targetFolder = targetFolder;
        _urlOverride = urlOverride;
        _browserPathOverride = browserPathOverride;
        _maxPixels = maxPixels > 0 ? maxPixels : DefaultMaxPixels;
        _headless = headless;
        _allowTestTarget = allowTestTarget;
        _extraBrowserArgs = extraBrowserArgs;
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
        if (_allowTestTarget) return IsTestOriginAuthorized(parsed);
        return TargetUrlPolicy.IsSingleWindowOrigin(url);
    }

    /// <summary>Test targets are explicit loopback/.test hosts only; never enabled by default.</summary>
    private bool IsTestOriginAuthorized(Uri parsed)
    {
        if (!_allowTestTarget) return false;
        if (_testOrigin is not null &&
            parsed.Scheme.Equals(_testOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
            parsed.Host.Equals(_testOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
            parsed.Port == _testOrigin.Port)
            return true;
        var host = parsed.Host;
        if (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                   parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        return host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The main page must be the exact single-window inquiry route; a URL merely containing
    /// "singlewindow" (for example https://example.com/?singlewindow) is not accepted.
    /// </summary>
    private bool IsTargetUrl(string? url) =>
        _allowTestTarget ? MatchesTargetOrigin(url, out _) : TargetUrlPolicy.IsSingleWindowInquiry(url);

    /// <summary>
    /// Sub-frames must be either the trusted single-window origin (same page) or the one
    /// verified official query frame; the card lives in the main frame.
    /// </summary>
    private bool IsTargetFrameUrl(string? url, string frameId) =>
        frameId.Equals(_mainFrameId, StringComparison.Ordinal)
            ? IsTargetUrl(url)
            : _allowTestTarget ? MatchesTargetOrigin(url, out _) : TargetUrlPolicy.IsAuthorizedFrame(url);

    /// <summary>
    /// A web request is only honored when its execution context belongs to an allowed frame
    /// of the target origin. An unknown frame or URL is refused instead of falling back to
    /// the top-level URL, so a nested attacker frame cannot borrow the page's authority.
    /// </summary>
    private bool IsAuthorizedContext(int contextId, string? sessionId)
    {
        FrameContext? context;
        lock (_frameContexts)
            context = _frameContexts.FirstOrDefault(x => x.ContextId == contextId && string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        if (context is null || string.IsNullOrEmpty(context.FrameId)) return false;
        return IsAuthorizedFrameId(context.FrameId);
    }

    /// <summary>Explains an authorization refusal in the log (registered frame, its URL and the main frame).</summary>
    private string DescribeContextForLog(int contextId, string? sessionId)
    {
        FrameContext? context;
        lock (_frameContexts)
            context = _frameContexts.FirstOrDefault(x => x.ContextId == contextId && string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        string? frameUrl = null;
        string main, current;
        lock (_frameUrls)
        {
            if (context is not null) _frameUrls.TryGetValue(context.FrameId, out frameUrl);
            main = _mainFrameId;
            current = _currentUrl;
        }
        return context is null
            ? $"未登记执行上下文 · main={main} · url={current}"
            : $"frame={context.FrameId} · frameUrl={frameUrl ?? "(未知)"} · main={main} · url={current}";
    }

    private bool IsAuthorizedFrameId(string frameId)
    {
        string? frameUrl;
        lock (_frameUrls) _frameUrls.TryGetValue(frameId, out frameUrl);
        if (string.IsNullOrWhiteSpace(frameUrl) && frameId.Equals(_mainFrameId, StringComparison.Ordinal))
            frameUrl = _currentUrl;
        return !string.IsNullOrWhiteSpace(frameUrl) && IsTargetFrameUrl(frameUrl, frameId);
    }

    /// <summary>Snapshot of the currently authorized default contexts (session-aware).</summary>
    private List<FrameContext> AuthorizedContexts()
    {
        List<FrameContext> snapshot;
        lock (_frameContexts) snapshot = _frameContexts.ToList();
        return snapshot.Where(x => !string.IsNullOrEmpty(x.FrameId) && IsAuthorizedFrameId(x.FrameId)).ToList();
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
        var profileRoot = Path.Combine(AppLog.Folder, "BrowserProfiles");
        // Every session uses a throw-away profile; sweep the ones earlier runs could not delete
        // (a crash or a browser that outlived the app) so they do not accumulate on disk.
        _ = Task.Run(() => PurgeStaleProfiles(profileRoot, TimeSpan.FromHours(12)));
        var profile = Path.Combine(profileRoot, SessionId);
        _profileFolder = profile;
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
        if (_extraBrowserArgs is not null)
            foreach (var argument in _extraBrowserArgs) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(url);
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动浏览器。");
        _process.EnableRaisingEvents = true;
        _process.Exited += OnBrowserExited;

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
        lock (_frameParents) _frameParents.Clear();
        lock (_frameContexts) _frameContexts.Clear();
        lock (_frameTargetSessions) _frameTargetSessions.Clear();
        Interlocked.Exchange(ref _navigationGeneration, 0);
        _shellScriptIdentifier = null;
        _cdp = await CdpClient.ConnectAsync(websocket, token);
        _cdp.Closed += OnConnectionClosed;
        _cdp.On("Runtime.executionContextCreated", OnContextCreated);
        _cdp.On("Runtime.executionContextDestroyed", OnContextDestroyed);
        _cdp.On("Runtime.executionContextsCleared", OnContextsCleared);
        _cdp.On("Runtime.bindingCalled", OnBindingCalled);
        _cdp.On("Page.frameNavigated", OnFrameNavigated);
        _cdp.On("Target.attachedToTarget", OnAttachedToTarget);
        _cdp.On("Target.detachedFromTarget", OnDetachedFromTarget);
        await _cdp.SendAsync("Page.enable", null, token);
        await _cdp.SendAsync("Runtime.enable", null, token);
        await _cdp.SendAsync("DOM.enable", null, token);
        // Flattened auto-attach so a cross-site (out-of-process) query iframe is reachable
        // through its own CDP session instead of being invisible to page-session evaluation.
        try
        {
            await _cdp.SendAsync("Target.setAutoAttach",
                new { autoAttach = true, waitForDebuggerOnStart = false, flatten = true }, token);
        }
        catch (Exception ex) { AppLog.Write($"启用子目标自动附加失败：{ex.Message}"); }
        // Seed the current URL/frame so binding validation works before the first
        // navigation event arrives. The complete frame tree is registered, not just the
        // root, so an already-loaded sub-frame is authorized (or refused) by its own URL.
        // The snapshot can predate a navigation that commits while it is in flight (the
        // initial document then reports an empty URL), and the frameNavigated event for that
        // commit may already have been applied. The seed therefore only fills values that are
        // still unknown and never replaces what a newer event recorded.
        try
        {
            var tree = await _cdp.SendAsync("Page.getFrameTree", null, token);
            var frameTree = tree.GetProperty("result").GetProperty("frameTree");
            var frames = new List<(string FrameId, string Url)>();
            CollectFrames(frameTree, frames);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectFrameParents(frameTree, parents);
            var root = frameTree.GetProperty("frame");
            var rootId = root.GetProperty("id").GetString() ?? "";
            var rootUrl = root.TryGetProperty("url", out var current) ? current.GetString() ?? "" : "";
            lock (_frameUrls)
            {
                foreach (var (id, url) in frames)
                    if (id.Length > 0 && url.Length > 0) _frameUrls.TryAdd(id, url);
                if (_mainFrameId.Length == 0) _mainFrameId = rootId;
                if (_currentUrl.Length == 0 && rootId.Equals(_mainFrameId, StringComparison.Ordinal)) _currentUrl = rootUrl;
            }
            lock (_frameParents)
                foreach (var pair in parents) _frameParents.TryAdd(pair.Key, pair.Value);
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

    private bool BrowserExited
    {
        get
        {
            try { return _process is { HasExited: true }; }
            catch (InvalidOperationException) { return true; }
        }
    }

    private void OnBrowserExited(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _endedRaised, 1) != 0) return;
        AppLog.Write($"核验浏览器已退出：{DeclarationNo}");
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void OnConnectionClosed(string reason)
    {
        // A closed browser window is the normal end of a session, not a connection fault.
        if (_disposed || BrowserExited) return;
        StatusChanged?.Invoke(this, "浏览器连接中断，正在尝试恢复……");
        // Keep an explicit recovery task running so the status message is truthful.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 8 && !_disposed && !BrowserExited; attempt++)
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

    private static string? SessionIdOf(JsonElement message) =>
        message.TryGetProperty("sessionId", out var session) ? session.GetString() : null;

    private void OnContextCreated(JsonElement message)
    {
        try
        {
            var sessionId = SessionIdOf(message);
            var context = message.GetProperty("params").GetProperty("context");
            if (!context.TryGetProperty("auxData", out var aux)) return;
            if (!aux.TryGetProperty("isDefault", out var isDefault) || !isDefault.GetBoolean()) return;
            var id = context.GetProperty("id").GetInt32();
            var frameId = aux.TryGetProperty("frameId", out var frame) ? frame.GetString() ?? "" : "";
            lock (_frameContexts)
            {
                _frameContexts.RemoveAll(x => x.ContextId == id && string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
                _frameContexts.Add(new FrameContext(id, frameId, sessionId));
            }
            // Install the click monitor as soon as a frame's JS context exists, so a query
            // click that fires shortly after load is observed even in a late-loaded OOPIF.
            if (frameId.Length > 0 && (IsAuthorizedFrameId(frameId) || IsPendingChildFrame(frameId)))
            {
                var monitor = MonitorScript();
                _ = Task.Run(async () =>
                {
                    var cdp = _cdp;
                    if (cdp is null) return;
                    try { await EvaluateContextAsync(monitor, id, sessionId, CancellationToken.None, awaitPromise: true); }
                    catch { }
                });
            }
        }
        catch (Exception ex) { AppLog.Write($"读取执行上下文失败：{ex.Message}"); }
    }

    /// <summary>A navigated-away frame must not leave a stale context id in the active set.</summary>
    private void OnContextDestroyed(JsonElement message)
    {
        try
        {
            var sessionId = SessionIdOf(message);
            var id = message.GetProperty("params").GetProperty("executionContextId").GetInt32();
            lock (_frameContexts)
                _frameContexts.RemoveAll(x => x.ContextId == id && string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        }
        catch (Exception ex) { AppLog.Write($"清理执行上下文失败：{ex.Message}"); }
    }

    private void OnContextsCleared(JsonElement message)
    {
        var sessionId = SessionIdOf(message);
        lock (_frameContexts)
        {
            if (sessionId is null) _frameContexts.Clear();
            else _frameContexts.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>A flattened child target (typically an OOPIF query iframe) was attached.</summary>
    private void OnAttachedToTarget(JsonElement message)
    {
        try
        {
            var parameters = message.GetProperty("params");
            var sessionId = parameters.TryGetProperty("sessionId", out var session) ? session.GetString() ?? "" : "";
            var targetInfo = parameters.GetProperty("targetInfo");
            var targetId = targetInfo.TryGetProperty("targetId", out var target) ? target.GetString() ?? "" : "";
            if (targetId.Length == 0 || sessionId.Length == 0) return;
            // For an iframe target the target id is the frame id.
            lock (_frameTargetSessions) _frameTargetSessions[targetId] = sessionId;
            // Register the target URL so the flattened OOPIF frame is authorized by its own
            // URL. Its Page.frameNavigated is delivered on the child session and can fire
            // before that session enabled Page, which previously left the frame URL unknown
            // and the frame permanently unauthorized (identity stayed waiting|not-started).
            var attachUrl = targetInfo.TryGetProperty("url", out var attachUrlProperty) ? attachUrlProperty.GetString() ?? "" : "";
            if (attachUrl.Length > 0 && !attachUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                lock (_frameUrls) _frameUrls[targetId] = attachUrl;
            _ = Task.Run(async () =>
            {
                var cdp = _cdp;
                if (cdp is null) return;
                try { await cdp.SendAsync("Runtime.enable", null, CancellationToken.None, sessionId); } catch { }
                try { await cdp.SendAsync("Page.enable", null, CancellationToken.None, sessionId); } catch { }
                try { await cdp.SendAsync("DOM.enable", null, CancellationToken.None, sessionId); } catch { }
                // The attach-time URL can still be about:blank (or the navigation committed
                // before Page.enable, which drops the frameNavigated event). Ask the child
                // session for its frame tree once so the real URL is registered either way.
                try
                {
                    var tree = await cdp.SendAsync("Page.getFrameTree", null, CancellationToken.None, sessionId);
                    var rootFrame = tree.GetProperty("result").GetProperty("frameTree").GetProperty("frame");
                    var rootFrameId = rootFrame.GetProperty("id").GetString() ?? "";
                    var rootUrl = rootFrame.TryGetProperty("url", out var rootUrlProperty) ? rootUrlProperty.GetString() ?? "" : "";
                    if (rootFrameId.Length > 0 && rootUrl.Length > 0)
                        lock (_frameUrls) _frameUrls[rootFrameId] = rootUrl;
                }
                catch { }
                // A child target (typically an OOPIF query frame) needs the click monitor at
                // document start too, otherwise a query that completes before the C# side
                // injects the monitor is never observed and identity stays "waiting". The
                // monitor only; the capture card must stay in the main frame.
                try
                {
                    await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                        new { source = MonitorScript() }, CancellationToken.None, sessionId);
                }
                catch { }
                try
                {
                    await cdp.SendAsync("Target.setAutoAttach",
                        new { autoAttach = true, waitForDebuggerOnStart = false, flatten = true }, CancellationToken.None, sessionId);
                }
                catch { }
            });
        }
        catch (Exception ex) { AppLog.Write($"处理子目标附加失败：{ex.Message}"); }
    }

    private void OnDetachedFromTarget(JsonElement message)
    {
        try
        {
            var sessionId = message.GetProperty("params").TryGetProperty("sessionId", out var session) ? session.GetString() : null;
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (_frameTargetSessions)
                foreach (var key in _frameTargetSessions.Where(x => x.Value == sessionId).Select(x => x.Key).ToList())
                    _frameTargetSessions.Remove(key);
            lock (_frameContexts) _frameContexts.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        }
        catch (Exception ex) { AppLog.Write($"处理子目标分离失败：{ex.Message}"); }
    }

    private void OnFrameNavigated(JsonElement message)
    {
        try
        {
            // Events carry the flattened child sessionId when they come from an attached
            // OOPIF. Only the page's own (root) session can speak for the top-level frame; a
            // child target reports its own root frame with no parentId, so parentId alone is
            // not enough to identify the page main frame.
            var sessionId = SessionIdOf(message);
            var frame = message.GetProperty("params").GetProperty("frame");
            var frameId = frame.GetProperty("id").GetString() ?? "";
            var url = frame.TryGetProperty("url", out var value) ? value.GetString() ?? "" : "";
            var parentId = frame.TryGetProperty("parentId", out var parent) ? parent.GetString() ?? "" : "";
            var isMain = sessionId is null && parentId.Length == 0;
            lock (_frameUrls)
            {
                if (frameId.Length > 0) _frameUrls[frameId] = url;
                if (isMain) { _mainFrameId = frameId; _currentUrl = url; }
            }
            // A child-session frame without parentId must keep its already known parent link
            // (its owner lives in the parent document); never overwrite a real parent with an
            // empty string. The root main frame legitimately has no parent and is left unset.
            if (frameId.Length > 0 && parentId.Length > 0)
                lock (_frameParents) _frameParents[frameId] = parentId;
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
        // script, so the monitor is installed into the main frame and every frame that is
        // authorized or still pending (its URL is not known yet). Result/read/execute
        // authorization stays strict elsewhere, so a pending frame that later turns out to
        // be foreign can never contribute a verdict. The card belongs to the main frame.
        try { await InjectMonitorAsync(token); }
        catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"向子框架安装监视器失败：{ex.Message}"); }
        try { await EvaluateContextAsync(ShellScript(), null, token, awaitPromise: true); }
        catch (Exception ex) when (IsConnectionFailure(ex)) { }
    }

    /// <summary>
    /// Installs the click monitor in the main frame and every frame that is authorized, plus
    /// direct children whose URL has not been reported yet. A frame already known to be
    /// unauthorized is left untouched.
    /// </summary>
    private async Task InjectMonitorAsync(CancellationToken token)
    {
        if (_cdp is null) return;
        var monitor = MonitorScript();
        List<FrameContext> contexts;
        lock (_frameContexts) contexts = _frameContexts.ToList();
        foreach (var context in contexts)
        {
            if (token.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(context.FrameId)) continue;
            if (!IsAuthorizedFrameId(context.FrameId) && !IsPendingChildFrame(context.FrameId)) continue;
            try { await EvaluateContextAsync(monitor, context.ContextId, context.SessionId, token, awaitPromise: true); }
            catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"向子框架安装监视器失败：{ex.Message}"); }
        }
        try { await EvaluateContextAsync(monitor, null, null, token, awaitPromise: true); }
        catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"向主框架安装监视器失败：{ex.Message}"); }
    }

    private bool IsUnknownFrameUrl(string frameId)
    {
        string? url;
        lock (_frameUrls)
        {
            if (!_frameUrls.TryGetValue(frameId, out url) && frameId.Equals(_mainFrameId, StringComparison.Ordinal)) url = _currentUrl;
        }
        if (string.IsNullOrWhiteSpace(url)) return true;
        // An in-process about:blank frame is a real, settled state (its navigations arrive on
        // the root session). A child target still at about:blank has not reported its commit.
        return url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && SessionForFrame(frameId) is not null;
    }

    /// <summary>
    /// Resolves frames whose URL is still unknown (or about:blank) from the authoritative CDP
    /// frame trees: the root session for in-process frames and each attached child session for
    /// OOPIFs. An OOPIF usually attaches with an empty URL and its commit can land before the
    /// child session enabled Page, so the one-shot lookup at attach time is not enough; without
    /// this the frame stays unauthorized and its query result is never seen. Only unknown
    /// entries are filled, so a URL recorded by a (newer) navigation event is never replaced.
    /// </summary>
    private async Task RefreshUnknownFrameUrlsAsync(CancellationToken token)
    {
        var cdp = _cdp;
        if (cdp is null || cdp.IsClosed) return;
        List<string> known;
        lock (_frameContexts) known = _frameContexts.Select(x => x.FrameId).Where(x => x.Length > 0).Distinct().ToList();
        lock (_frameTargetSessions) known.AddRange(_frameTargetSessions.Keys);
        if (_mainFrameId.Length > 0) known.Add(_mainFrameId);
        var unknown = known.Distinct(StringComparer.Ordinal).Where(IsUnknownFrameUrl).ToList();
        if (unknown.Count == 0) return;

        void Fill(string frameId, string url)
        {
            // A still-blank document is not an answer; leave it unknown so a later call retries.
            if (frameId.Length == 0 || url.Length == 0 || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            // Only a child target may replace a recorded about:blank (its commit event can be
            // lost); an in-process or main frame at about:blank is a settled newer state.
            var childTarget = SessionForFrame(frameId) is not null;
            lock (_frameUrls)
            {
                if (_frameUrls.TryGetValue(frameId, out var existing) && existing.Length > 0 &&
                    !(childTarget && existing.StartsWith("about:", StringComparison.OrdinalIgnoreCase))) return;
                _frameUrls[frameId] = url;
                if (frameId.Equals(_mainFrameId, StringComparison.Ordinal) && _currentUrl.Length == 0) _currentUrl = url;
            }
        }

        try
        {
            var tree = await cdp.SendAsync("Page.getFrameTree", null, token);
            var frames = new List<(string FrameId, string Url)>();
            CollectFrames(tree.GetProperty("result").GetProperty("frameTree"), frames);
            foreach (var (id, url) in frames) Fill(id, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write($"刷新框架地址失败：{ex.Message}"); }

        foreach (var frameId in unknown.Where(IsUnknownFrameUrl))
        {
            var session = SessionForFrame(frameId);
            if (session is null) continue;
            try
            {
                var tree = await cdp.SendAsync("Page.getFrameTree", null, token, session);
                var frame = tree.GetProperty("result").GetProperty("frameTree").GetProperty("frame");
                Fill(frame.GetProperty("id").GetString() ?? "", frame.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write($"刷新子框架地址失败 {frameId}：{ex.Message}"); }
        }
    }

    private bool IsPendingChildFrame(string frameId)
    {
        string? url;
        lock (_frameUrls) _frameUrls.TryGetValue(frameId, out url);
        if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return false;
        string? parent;
        lock (_frameParents) _frameParents.TryGetValue(frameId, out parent);
        return string.IsNullOrEmpty(parent) || parent.Equals(_mainFrameId, StringComparison.Ordinal);
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
            // A query frame (including a cross-site OOPIF) can finish loading after the
            // initial injection; keep installing the click monitor so the query click is
            // observed wherever the form actually lives.
            try { await InjectMonitorAsync(token); }
            catch (Exception ex) when (!IsConnectionFailure(ex)) { AppLog.Write($"向子框架安装监视器失败：{ex.Message}"); }
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
            var sessionId = SessionIdOf(message);
            if (IsAuthorizedContext(contextId, sessionId))
            {
                HandleAuthorizedBinding(name, contextId);
                return;
            }
            // The frame URL can still be unknown right after (re)connecting or attaching: the
            // frame-tree snapshot may predate the commit. Resolve it from CDP once before
            // judging, instead of refusing a legitimate click on the target page.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    refresh.CancelAfter(TimeSpan.FromSeconds(5));
                    await RefreshUnknownFrameUrlsAsync(refresh.Token);
                }
                catch (Exception ex) { AppLog.Write($"复核网页请求来源失败：{ex.Message}"); }
                if (IsAuthorizedContext(contextId, sessionId))
                {
                    HandleAuthorizedBinding(name, contextId);
                    return;
                }
                AppLog.Write($"已忽略来源页面的网页请求：{name} · context={contextId} · session={sessionId ?? "-"} · {DescribeContextForLog(contextId, sessionId)}");
                await SetWidgetStateAsync("error", "当前页面不是核验目标页，已忽略网页请求。");
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"处理网页请求失败：{ex.Message}");
        }
    }

    private void HandleAuthorizedBinding(string? name, int contextId)
    {
        try
        {
            if (name == "cccRequestCapture")
            {
                if (Interlocked.CompareExchange(ref _captureGate, 1, 0) != 0)
                {
                    // Another capture already owns the single-owner gate. Reject without
                    // touching the gate: the running capture publishes the next retryable
                    // page state, so a card that switched itself to capturing does not need a
                    // second completion event to recover.
                    AppLog.Write($"忙拒绝网页截图请求：已有截图正在进行（context={contextId}）。");
                    return;
                }
                AppLog.Write($"接受网页截图请求并持有截图锁（context={contextId}）。");
                _ = Task.Run(async () =>
                {
                    try { await CaptureAndReportAsync(); }
                    catch (Exception ex) { AppLog.Write($"截图任务异常：{ex.Message}"); }
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
        }
        finally
        {
            // The capture and every page restore are finished, so this request no longer owns
            // the capture resource. Release the single-owner gate here, before the retryable
            // page state and the completion event are published, so a fast retry whose real
            // click lands right after the button is re-enabled can never be delivered into a
            // still-held gate and swallowed. This is the only release: the busy path never
            // touches the gate, and the finally keeps the gate from being held forever on a
            // failure, cancellation or reporting exception.
            Interlocked.Exchange(ref _captureGate, 0);
            AppLog.Write("网页截图锁已释放。");
        }
        AppLog.Write($"网页截图结论：{result.State}。");
        await ReportAsync(result);
        // Publish the retryable page state only after the card is interactively painted again.
        // The capture hid the card and may have restored an enlarged viewport; a completion
        // published before the card is painted could let a fast retry's real click be lost
        // (a compositor/paint risk to verify, not a proven cause). The wait is bounded and
        // best-effort: its outcome is recorded as a separate recovery signal and must never
        // change the saved-file verdict or suppress the completion event.
        LastCompletionReadiness = "";
        try
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ready.CancelAfter(TimeSpan.FromSeconds(3));
            LastCompletionReadiness = await WaitForWidgetInteractiveAsync(TimeSpan.FromSeconds(2), _captureBaselineViewport, ready.Token);
            if (!TryParseReadiness(LastCompletionReadiness, out var completionReadiness)
                || !completionReadiness.Ready || !completionReadiness.ExactButtonHit
                || !completionReadiness.Enabled || !completionReadiness.Visible)
                AppLog.Write($"截图完成发布前卡片未恢复可交互（独立于保存结论，不改变 {result.State}）：{Shorten(LastCompletionReadiness)}");
        }
        catch (Exception ex)
        {
            LastCompletionReadiness = "就绪探测失败:" + ex.Message;
            AppLog.Write($"等待卡片恢复可交互失败（独立于保存结论，不改变 {result.State}）：{ex.Message}");
        }
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
        // The readiness published with this capture's completion must target the viewport
        // observed before this capture enlarged it; a stale value from a previous capture
        // could make the completion wait for the wrong size.
        _captureBaselineViewport = null;

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
        var preparedTargets = new List<EvalTarget>();
        var expandedFrames = new List<string>();
        var capturedOopif = new List<OopifPaintRange>();
        try
        {
            try
            {
                await PrepareContextsAsync(BrowserScript("capture-prepare"), preparedTargets, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null, $"网页准备失败，未保存截图：{ex.Message}");
            }
            await Task.Delay(300, token);

            // Cross-origin frames cannot be resized from page JS. The CDP frame owner is
            // grown to the frame document height so the complete frame content is painted;
            // a frame that cannot be expanded rejects the capture instead of truncating it.
            var failedFrames = await ExpandFramesAsync(expandedFrames, capturedOopif, token);
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

            // captureBeyondViewport does not reliably re-render an out-of-process iframe whose
            // painted box extends past the real viewport: the region below the viewport is
            // captured blank. Every authorized attached child-session frame that participates in
            // the capture is recorded above with its painted extent, whether or not this run had
            // to grow its owner; the real viewport is enlarged whenever the clip or any such
            // frame exceeds the current viewport. Reading the real viewport is therefore a
            // required prerequisite here, not an optional optimization: when a child-session
            // frame participates and the viewport cannot be read reliably, the capture is
            // rejected instead of falling back to the known-blank path. The override, including
            // the original device pixel ratio, is cleared in the finally below.
            var viewport = capturedOopif.Count > 0 ? await TryReadViewportAsync(token) : null;
            if (capturedOopif.Count > 0 && viewport is null)
                return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null,
                    "无法读取浏览器实际视口尺寸，为避免保存空白跨进程框架截图，已拒绝本次截图。");
            _captureBaselineViewport = viewport;
            var viewportWidth = 0;
            var viewportHeight = 0;
            var viewportScale = 1.0;
            if (viewport is { } readViewport)
            {
                viewportWidth = readViewport.Width;
                viewportHeight = readViewport.Height;
                viewportScale = readViewport.Scale;
            }
            var enlargedViewport = viewport is not null
                && (width > viewportWidth || height > viewportHeight
                    || capturedOopif.Any(frame => frame.Bottom > viewportHeight || frame.Right > viewportWidth));
            var paintWidth = enlargedViewport ? Math.Max(viewportWidth, width) : viewportWidth;
            var paintHeight = enlargedViewport ? Math.Max(viewportHeight, height) : viewportHeight;
            try
            {
                if (enlargedViewport)
                {
                    await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new
                    {
                        width = paintWidth,
                        height = paintHeight,
                        deviceScaleFactor = viewportScale,
                        mobile = false
                    }, token);
                    // Never capture on a fixed delay: under load the out-of-process frame has not
                    // yet learned that it is fully visible and its lower part is saved blank. Wait
                    // (bounded) until the page reports the enlarged viewport and every captured
                    // child frame reports itself completely inside it, then for fresh frames.
                    var paintProblem = await WaitForEnlargedPaintAsync(paintWidth, paintHeight, capturedOopif, token);
                    if (paintProblem is not null)
                        return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null,
                            $"跨进程网页框架未能在放大视口后完成重绘（{paintProblem}），为避免保存空白截图，已拒绝本次截图。");
                    // The enlarged viewport can change the content size; re-read and never
                    // shrink below the frame content that was already grown.
                    var expandedMetrics = await cdp.SendAsync("Page.getLayoutMetrics", null, token);
                    var expandedContent = expandedMetrics.GetProperty("result").GetProperty("contentSize");
                    width = Math.Max(width, (int)Math.Ceiling(expandedContent.GetProperty("width").GetDouble()));
                    height = Math.Max(height, (int)Math.Ceiling(expandedContent.GetProperty("height").GetDouble()));
                    if ((long)width * height > _maxPixels || width > 16_384 || height > 65_535)
                        return new BrowserCaptureResult(SessionId, DeclarationNo, "too-long", null, "页面超过 6000 万像素，未保存截图；请使用浏览器分段保存。");
                }

                var tiles = (int)Math.Ceiling(height / (double)TileHeight);
                using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                for (var attempt = 1; ; attempt++)
                {
                    await ReportProgressAsync(0, tiles, "正在截取整页");
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
                            using var tile = await CaptureClipAsync(cdp, 0, y, width, tileHeight, token);
                            graphics.DrawImageUnscaled(tile, 0, y);
                            await ReportProgressAsync(index + 1, tiles, null);
                        }
                    }
                    // Even after the frame reported itself visible, its tiles can still be rastered
                    // after the capture drew the frame (seen 1/80 under load): the saved band is blank
                    // while a later look shows content. Re-check each out-of-process frame's bottom
                    // band against a fresh clip and capture again instead of saving a blank frame.
                    if (capturedOopif.Count == 0) break;
                    var paint = await CheckOopifPaintAsync(cdp, full, capturedOopif, token);
                    if (paint == OopifPaintCheck.Consistent) break;
                    AppLog.Write($"跨进程框架绘制复核第 {attempt} 次：{paint}。");
                    if (attempt >= 3)
                    {
                        if (paint == OopifPaintCheck.Unpainted)
                            return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null,
                                "跨进程网页框架多次截取仍未完成绘制，为避免保存空白截图，已拒绝本次截图。");
                        // The frame is painted; only its content keeps changing (for example an
                        // animation), which the result fingerprint check below still guards.
                        break;
                    }
                    var repaintProblem = await WaitForEnlargedPaintAsync(paintWidth, paintHeight, capturedOopif, token);
                    if (repaintProblem is not null)
                        return new BrowserCaptureResult(SessionId, DeclarationNo, "error", null,
                            $"跨进程网页框架未能完成重绘（{repaintProblem}），为避免保存空白截图，已拒绝本次截图。");
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
                if (enlargedViewport)
                {
                    try { await cdp.SendAsync("Emulation.clearDeviceMetricsOverride", null, CancellationToken.None); }
                    catch (Exception ex) { AppLog.Write($"恢复视口尺寸失败：{ex.Message}"); }
                }
            }
        }
        finally
        {
            // Restoration must run even when the capture token was cancelled. Frame heights
            // are restored through CDP first (cross-origin included), then the page-level
            // script puts back scroll offsets, overflow and widget visibility.
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await RestoreFramesAsync(expandedFrames, restore.Token);
            await RestoreContextsAsync(BrowserScript("capture-restore"), preparedTargets, restore.Token);
        }
    }

    /// <summary>
    /// Reads the real CSS viewport and device pixel ratio the capture must enlarge to paint an
    /// out-of-process frame. Transient CDP/script failures and non-positive readings are retried
    /// because an unreadable viewport must never be mistaken for "no enlargement needed" and
    /// fall back to the blank capture path; the caller rejects when this still returns null.
    /// </summary>
    private async Task<(int Width, int Height, double Scale)?> TryReadViewportAsync(CancellationToken token)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Volatile.Read(ref _viewportReadFailures) > 0)
                {
                    Interlocked.Decrement(ref _viewportReadFailures);
                    AppLog.Write($"注入视口读取失败（第 {attempt}/3 次）。");
                }
                else
                {
                    using var document = JsonDocument.Parse(await EvaluateContextAsync(
                        "JSON.stringify({w:window.innerWidth||0,h:window.innerHeight||0,dpr:window.devicePixelRatio||1})",
                        null, token, awaitPromise: false));
                    var width = document.RootElement.GetProperty("w").GetInt32();
                    var height = document.RootElement.GetProperty("h").GetInt32();
                    var scale = document.RootElement.TryGetProperty("dpr", out var dpr) && dpr.GetDouble() > 0 ? dpr.GetDouble() : 1;
                    if (width > 0 && height > 0) return (width, height, scale);
                    AppLog.Write($"读取视口尺寸无效（第 {attempt}/3 次）：w={width},h={height}。");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Write($"读取视口尺寸失败（第 {attempt}/3 次）：{ex.Message}");
            }
            if (attempt < 3) await Task.Delay(150, token);
        }
        return null;
    }

    private async Task<Bitmap> CaptureClipAsync(CdpClient cdp, int x, int y, int width, int height, CancellationToken token)
    {
        var response = await cdp.SendAsync("Page.captureScreenshot", new
        {
            format = "png",
            fromSurface = true,
            captureBeyondViewport = true,
            clip = new { x, y, width, height, scale = 1 }
        }, token);
        var data = Convert.FromBase64String(response.GetProperty("result").GetProperty("data").GetString()!);
        using var stream = new MemoryStream(data);
        // Bitmap(Stream) keeps reading the stream lazily; copy so the result owns its pixels.
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    private enum OopifPaintCheck { Consistent, Changed, Unpainted }

    /// <summary>
    /// Compares the bottom band of every captured out-of-process frame in the stitched image
    /// with a fresh clip of the same page area. Unpainted: the stitched band is a single flat
    /// colour while the fresh clip is not (the frame was drawn before its tiles existed).
    /// Changed: the bands differ otherwise (live content). Consistent: they match.
    /// </summary>
    private async Task<OopifPaintCheck> CheckOopifPaintAsync(CdpClient cdp, Bitmap full, IReadOnlyList<OopifPaintRange> frames, CancellationToken token)
    {
        var result = OopifPaintCheck.Consistent;
        foreach (var frame in frames)
        {
            var left = Math.Clamp(frame.Left, 0, full.Width - 1);
            var right = Math.Clamp(frame.Right, left + 1, full.Width);
            var bottom = Math.Clamp(frame.Bottom, 1, full.Height);
            var top = Math.Max(0, bottom - 360);
            if (right - left < 4 || bottom - top < 4) continue;
            using var fresh = await CaptureClipAsync(cdp, left, top, right - left, bottom - top, token);
            var width = Math.Min(fresh.Width, right - left);
            var height = Math.Min(fresh.Height, bottom - top);
            var samples = 0;
            var differences = 0;
            var stitchedFlat = true;
            var freshFlat = true;
            var stitchedFirst = full.GetPixel(left, top);
            var freshFirst = fresh.GetPixel(0, 0);
            for (var y = 0; y < height; y += 3)
                for (var x = 0; x < width; x += 3)
                {
                    var a = full.GetPixel(left + x, top + y);
                    var b = fresh.GetPixel(x, y);
                    samples++;
                    if (!Near(a, b)) differences++;
                    if (stitchedFlat && !Near(a, stitchedFirst)) stitchedFlat = false;
                    if (freshFlat && !Near(b, freshFirst)) freshFlat = false;
                }
            if (samples == 0 || differences <= samples / 200) continue;
            if (stitchedFlat && !freshFlat) return OopifPaintCheck.Unpainted;
            result = OopifPaintCheck.Changed;
        }
        return result;

        static bool Near(Color a, Color b) =>
            Math.Abs(a.R - b.R) <= 8 && Math.Abs(a.G - b.G) <= 8 && Math.Abs(a.B - b.B) <= 8;
    }

    private const int PaintWaitMilliseconds = 8000;

    /// <summary>
    /// Resolves once the top-level page reports at least the enlarged viewport and has produced
    /// two further frames. Bounded: a missing frame clock resolves with a reason, never hangs.
    /// </summary>
    private static string MainViewportPaintScript(int width, int height) => FormattableString.Invariant($$"""
        new Promise(function (resolve) {
          var deadline = Date.now() + {{PaintWaitMilliseconds}}, done = false;
          function finish(v) { if (!done) { done = true; resolve(v); } }
          setTimeout(function () { finish('no-frames:' + window.innerWidth + 'x' + window.innerHeight); }, {{PaintWaitMilliseconds + 500}});
          function step() {
            if (done) return;
            if (window.innerWidth >= {{width}} - 1 && window.innerHeight >= {{height}} - 1) {
              requestAnimationFrame(function () { requestAnimationFrame(function () { finish('ready'); }); });
              return;
            }
            if (Date.now() > deadline) { finish('viewport:' + window.innerWidth + 'x' + window.innerHeight); return; }
            requestAnimationFrame(step);
          }
          requestAnimationFrame(step);
        })
        """);

    /// <summary>
    /// Runs inside an out-of-process frame: resolves once that frame's renderer reports its whole
    /// visible document inside the top-level viewport (IntersectionObserver uses the embedder's
    /// real viewport intersection, which is what the compositor paints by) and two further frames
    /// were produced. A fresh observer per frame always delivers the current state.
    /// </summary>
    private static readonly string OopifVisiblePaintScript = FormattableString.Invariant($$"""
        new Promise(function (resolve) {
          var deadline = Date.now() + {{PaintWaitMilliseconds}}, done = false, last = 'none';
          function finish(v) { if (!done) { done = true; resolve(v); } }
          setTimeout(function () { finish('no-frames:' + last); }, {{PaintWaitMilliseconds + 500}});
          var el = document.documentElement;
          if (!el || typeof IntersectionObserver !== 'function') { finish('unsupported'); return; }
          function check() {
            if (done) return;
            if (Date.now() > deadline) { finish('hidden:' + last); return; }
            var io = new IntersectionObserver(function (entries) {
              io.disconnect();
              var e = entries[entries.length - 1];
              var needH = Math.min(window.innerHeight, e.boundingClientRect.height) - 2;
              var needW = Math.min(window.innerWidth, e.boundingClientRect.width) - 2;
              last = Math.round(e.intersectionRect.width) + 'x' + Math.round(e.intersectionRect.height) + '/' + Math.round(needW + 2) + 'x' + Math.round(needH + 2);
              if (e.intersectionRect.height >= needH && e.intersectionRect.width >= needW)
                requestAnimationFrame(function () { requestAnimationFrame(function () { finish('visible'); }); });
              else requestAnimationFrame(check);
            });
            io.observe(el);
          }
          check();
        })
        """);

    /// <summary>
    /// Bounded paint-readiness gate after the real viewport was enlarged for an OOPIF capture.
    /// Returns null when every participant is ready, otherwise a short reason.
    /// </summary>
    private async Task<string?> WaitForEnlargedPaintAsync(int width, int height, IReadOnlyList<OopifPaintRange> frames, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMilliseconds(PaintWaitMilliseconds + 3000));
        try
        {
            var main = await EvaluateContextAsync(MainViewportPaintScript(width, height), null, bounded.Token, awaitPromise: true);
            if (main != "ready") return $"page={main}";
            foreach (var frame in frames)
            {
                var state = await EvaluateContextAsync(OopifVisiblePaintScript, frame.ContextId, frame.SessionId, bounded.Token, awaitPromise: true);
                if (state != "visible") return $"{frame.FrameId}={state}";
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "timeout";
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsConnectionFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Layout-independent result fingerprint used to detect in-flight changes. Only the
    /// top-level shell and the frames that actually hold the query result are consulted:
    /// "waiting" from the outer shell is not a verdict and must not short-circuit a ready
    /// query frame (R5-1).
    /// </summary>
    private Task<string> VerifyStableAsync(CancellationToken token) =>
        EvaluateAllContextsAsync(BrowserScript("verify", DeclarationNo), token, false,
            "stable|", "mismatch|", "error|", "stale|");

    private static bool IsPreparedMarker(string value) =>
        value.Contains("prepared:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("already-prepared", StringComparison.OrdinalIgnoreCase);

    /// <summary>A concrete place a capture script may run: a frame plus its CDP session/context.</summary>
    private sealed record EvalTarget(string FrameId, int? ContextId, string? SessionId);

    private string? SessionForFrame(string frameId)
    {
        lock (_frameTargetSessions) return _frameTargetSessions.TryGetValue(frameId, out var session) ? session : null;
    }

    private FrameContext? ContextForFrame(string frameId)
    {
        List<FrameContext> snapshot;
        lock (_frameContexts) snapshot = _frameContexts.ToList();
        return snapshot
            .Where(x => x.FrameId.Equals(frameId, StringComparison.Ordinal))
            .OrderBy(x => x.SessionId is null ? 0 : 1)
            .FirstOrDefault();
    }

    private async Task<int?> CreateIsolatedWorldAsync(string frameId, string worldName, string? session, CancellationToken token)
    {
        if (_cdp is null) return null;
        try
        {
            var world = session is null
                ? await _cdp.SendAsync("Page.createIsolatedWorld", new { frameId, worldName, grantUniveralAccess = true }, token)
                : await _cdp.SendAsync("Page.createIsolatedWorld", new { frameId, worldName, grantUniveralAccess = true }, token, session);
            return world.GetProperty("result").GetProperty("executionContextId").GetInt32();
        }
        catch (Exception ex)
        {
            AppLog.Write($"无法建立框架执行环境 {frameId}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Authorized evaluation targets: the main frame plus every authorized child frame. A
    /// frame that has not reported a default context (pre-existing frame or OOPIF) gets an
    /// isolated world on its owning CDP session so its DOM is still reachable.
    /// </summary>
    private async Task<List<EvalTarget>> AuthorizedEvalTargetsAsync(CancellationToken token)
    {
        await RefreshUnknownFrameUrlsAsync(token);
        var targets = new List<EvalTarget> { new(_mainFrameId, null, null) };
        List<string> frameIds;
        lock (_frameUrls) frameIds = _frameUrls.Keys.ToList();
        foreach (var frameId in frameIds)
        {
            if (frameId.Equals(_mainFrameId, StringComparison.Ordinal)) continue;
            if (!IsAuthorizedFrameId(frameId)) continue;
            token.ThrowIfCancellationRequested();
            var context = ContextForFrame(frameId);
            if (context is not null)
            {
                targets.Add(new EvalTarget(frameId, context.ContextId, context.SessionId));
                continue;
            }
            var session = SessionForFrame(frameId);
            var contextId = await CreateIsolatedWorldAsync(frameId, "customs-console-frame", session, token);
            targets.Add(new EvalTarget(frameId, contextId, session));
        }
        return targets;
    }

    private Task<string> EvaluateTargetAsync(string expression, EvalTarget target, CancellationToken token, bool awaitPromise) =>
        EvaluateContextAsync(expression, target.ContextId, target.SessionId, token, awaitPromise);

    /// <summary>
    /// Runs the prepare expression target by target. Every target is registered in the
    /// restore ledger BEFORE the command is sent, so a lost/failed/cancelled response still
    /// restores it; a missing prepared marker or a page script exception rejects the capture
    /// instead of silently saving a half-prepared page (R5-2).
    /// </summary>
    private async Task PrepareContextsAsync(string expression, List<EvalTarget> prepared, CancellationToken token)
    {
        if (_cdp is null) return;
        var targets = await AuthorizedEvalTargetsAsync(token);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            prepared.Add(target);
            string value;
            try
            {
                value = await EvaluateTargetAsync(expression, target, token, awaitPromise: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!IsConnectionFailure(ex))
            {
                throw new InvalidOperationException($"网页框架状态准备失败（{target.FrameId}）：{ex.Message}", ex);
            }
            if (!IsPreparedMarker(value))
                throw new InvalidOperationException($"网页框架状态准备未确认（{target.FrameId}）：{value}");
        }
    }

    private async Task RestoreContextsAsync(string expression, IReadOnlyList<EvalTarget> prepared, CancellationToken token)
    {
        if (_cdp is null) return;
        // Restore in reverse order so the main frame's window scroll is put back last.
        foreach (var target in prepared.Reverse())
        {
            if (token.IsCancellationRequested) break;
            try { await EvaluateTargetAsync(expression, target, token, awaitPromise: true); }
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

    private static void CollectFrameParents(JsonElement tree, Dictionary<string, string> parents)
    {
        if (!tree.TryGetProperty("frame", out var frame)) return;
        var id = frame.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
        var parent = frame.TryGetProperty("parentId", out var parentValue) ? parentValue.GetString() ?? "" : "";
        if (id.Length > 0 && parent.Length > 0) parents[id] = parent;
        if (tree.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) CollectFrameParents(child, parents);
    }

    /// <summary>The CDP session that owns a frame's parent document (where its owner element lives).</summary>
    private string? OwnerSessionForFrame(string frameId)
    {
        string? parent;
        lock (_frameParents) _frameParents.TryGetValue(frameId, out parent);
        if (string.IsNullOrEmpty(parent) || parent.Equals(_mainFrameId, StringComparison.Ordinal)) return null;
        return SessionForFrame(parent);
    }

    private async Task<string?> GetFrameOwnerObjectIdAsync(string frameId, CancellationToken token)
    {
        if (_cdp is null) return null;
        var session = OwnerSessionForFrame(frameId);
        var owner = session is null
            ? await _cdp.SendAsync("DOM.getFrameOwner", new { frameId }, token)
            : await _cdp.SendAsync("DOM.getFrameOwner", new { frameId }, token, session);
        if (!owner.GetProperty("result").TryGetProperty("backendNodeId", out var backend) || backend.GetInt32() <= 0) return null;
        var resolved = session is null
            ? await _cdp.SendAsync("DOM.resolveNode", new { backendNodeId = backend.GetInt32() }, token)
            : await _cdp.SendAsync("DOM.resolveNode", new { backendNodeId = backend.GetInt32() }, token, session);
        if (!resolved.GetProperty("result").TryGetProperty("object", out var remote)) return null;
        return remote.TryGetProperty("objectId", out var objectId) ? objectId.GetString() : null;
    }

    private async Task<string> CallFunctionOnAsync(string objectId, string function, object? argument, CancellationToken token, string? sessionId = null)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        object parameters = argument is null
            ? (object)new { objectId, functionDeclaration = function, returnByValue = true, awaitPromise = false }
            : new { objectId, functionDeclaration = function, arguments = new[] { argument }, returnByValue = true, awaitPromise = false };
        var response = await cdp.SendAsync("Runtime.callFunctionOn", parameters, token, sessionId);
        try
        {
            var result = response.GetProperty("result").GetProperty("result");
            return result.TryGetProperty("value", out var value) ? value.ToString() : "";
        }
        catch { return ""; }
    }

    private static readonly string ReadFrameGeometryFunction =
        "function(){ if(!this||this.tagName!=='IFRAME') return JSON.stringify({ok:false,reason:'not-iframe'});" +
        " var r=this.getBoundingClientRect(); var mh=getComputedStyle(this).maxHeight||'';" +
        " return JSON.stringify({ok:true,client:this.clientHeight||0,rect:Math.round(r.height),maxHeight:mh," +
        " bottom:Math.round(r.bottom+(window.scrollY||0)),right:Math.round(r.right+(window.scrollX||0)),left:Math.round(r.left+(window.scrollX||0))}); }";

    private static readonly string GrowFrameFunction =
        "function(h){ if(!this||this.tagName!=='IFRAME') return '0';" +
        " if(this.__cccPrevHeight===undefined){ this.__cccPrevHeight=this.style.getPropertyValue('height'); this.__cccPrevHeightPriority=this.style.getPropertyPriority('height'); }" +
        " if(this.__cccPrevMaxHeight===undefined){ this.__cccPrevMaxHeight=this.style.getPropertyValue('max-height'); this.__cccPrevMaxHeightPriority=this.style.getPropertyPriority('max-height'); }" +
        " this.style.setProperty('height', h+'px', 'important'); this.style.setProperty('max-height', 'none', 'important');" +
        " return String(h); }";

    private static readonly string RestoreFrameFunction =
        "function(){ if(!this||this.tagName!=='IFRAME') return 'ok';" +
        " if(this.__cccPrevHeight!==undefined){ if(this.__cccPrevHeight) this.style.setProperty('height', this.__cccPrevHeight, this.__cccPrevHeightPriority||''); else this.style.removeProperty('height'); delete this.__cccPrevHeight; delete this.__cccPrevHeightPriority; }" +
        " if(this.__cccPrevMaxHeight!==undefined){ if(this.__cccPrevMaxHeight) this.style.setProperty('max-height', this.__cccPrevMaxHeight, this.__cccPrevMaxHeightPriority||''); else this.style.removeProperty('max-height'); delete this.__cccPrevMaxHeight; delete this.__cccPrevMaxHeightPriority; }" +
        " return 'ok'; }";

    private static bool TryReadFrameGeometry(string json, out int visibleHeight, out double maxHeight, out int bottom, out int right, out int left)
    {
        left = 0;
        visibleHeight = 0;
        maxHeight = double.PositiveInfinity;
        bottom = 0;
        right = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return false;
            visibleHeight = root.TryGetProperty("client", out var client) ? client.GetInt32() : 0;
            bottom = root.TryGetProperty("bottom", out var bottomElement) ? bottomElement.GetInt32() : 0;
            right = root.TryGetProperty("right", out var rightElement) ? rightElement.GetInt32() : 0;
            left = root.TryGetProperty("left", out var leftElement) ? leftElement.GetInt32() : 0;
            if (root.TryGetProperty("maxHeight", out var max))
            {
                var text = max.GetString() ?? "";
                maxHeight = text.Equals("none", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity :
                    double.TryParse(text.Replace("px", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.PositiveInfinity;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// An attached child-session (out-of-process) frame that participates in the capture, with
    /// its owner box's page-space bottom/right. It is recorded whether or not the owner had to
    /// be grown, because the real viewport must be enlarged whenever such a frame is painted
    /// beyond it; tying that decision to "did we change the owner" misses a frame that was
    /// already tall enough for its content but still taller than the physical viewport.
    /// </summary>
    private sealed record OopifPaintRange(string FrameId, int Left, int Bottom, int Right, int ContextId, string? SessionId);

    /// <summary>
    /// Grows every authorized sub-frame's owner element to the frame document height using
    /// CDP (so cross-origin and OOPIF frames are included). It is not enough to write a
    /// height: the element's real rendered height, its computed max-height and the frame's
    /// own viewport are read back after a layout tick. A frame that cannot be fully exposed
    /// is reported so the capture fails instead of truncating (R5-3).
    /// </summary>
    private async Task<List<string>> ExpandFramesAsync(List<string> expanded, List<OopifPaintRange> capturedOopif, CancellationToken token)
    {
        var failed = new List<string>();
        if (_cdp is null) return failed;
        JsonElement tree;
        try { tree = await _cdp.SendAsync("Page.getFrameTree", null, token); }
        catch (Exception ex)
        {
            // A missing frame tree means the frame candidates could not be confirmed at all;
            // returning an empty failure list here would let an unverified capture save.
            AppLog.Write($"读取框架树失败：{ex.Message}");
            return [$"(root frame tree 读取失败：{ex.Message})"];
        }
        var frames = new List<(string FrameId, string Url)>();
        CollectFrames(tree.GetProperty("result").GetProperty("frameTree"), frames);
        // Refresh real URLs from the root tree, but never let an empty stub URL clobber the
        // child-session URL that attach/child-getFrameTree registered for an OOPIF.
        foreach (var (id, url) in frames)
            if (id.Length > 0 && url.Length > 0)
                lock (_frameUrls) _frameUrls[id] = url;
        // A flattened OOPIF is reached through its own child target and can be missing from
        // the root session's Page.getFrameTree. Enumerate the live attached child targets too
        // so the cross-process query frame is expanded and verified rather than silently
        // truncating the capture.
        var seenFrames = new HashSet<string>(frames.Select(x => x.FrameId), StringComparer.Ordinal);
        lock (_frameTargetSessions)
            foreach (var targetId in _frameTargetSessions.Keys)
                if (targetId.Length > 0 && seenFrames.Add(targetId)) frames.Add((targetId, ""));
        var candidateLog = frames.Select(x => x.FrameId + (IsAuthorizedFrameId(x.FrameId) ? "[授权]" : "[跳过]"));
        AppLog.Write("框架扩展候选：" + string.Join("、", candidateLog));
        foreach (var (frameId, _) in frames)
        {
            if (frameId.Equals(_mainFrameId, StringComparison.Ordinal)) continue;
            if (!IsAuthorizedFrameId(frameId)) continue;
            token.ThrowIfCancellationRequested();
            try
            {
                var context = ContextForFrame(frameId);
                var session = context?.SessionId ?? SessionForFrame(frameId);
                var contextId = context?.ContextId;
                if (contextId is null)
                    contextId = await CreateIsolatedWorldAsync(frameId, "customs-console-frame-expand", session, token);
                if (contextId is null) { AppLog.Write($"扩展框架 {frameId} 失败：无执行环境（session={session ?? "-"}）。"); failed.Add(frameId); continue; }

                var measured = await EvaluateContextAsync(
                    "JSON.stringify((function(){var d=document.documentElement,b=document.body;return {content:Math.max(d?d.scrollHeight:0,b?b.scrollHeight:0)};})())",
                    contextId, session, token, awaitPromise: false);
                int frameContent;
                try
                {
                    using var document = JsonDocument.Parse(measured);
                    frameContent = (int)Math.Ceiling(document.RootElement.GetProperty("content").GetDouble());
                }
                catch { AppLog.Write($"扩展框架 {frameId} 失败：内容高度读取异常（{measured}）。"); failed.Add(frameId); continue; }
                if (frameContent <= 0)
                {
                    // An attached child-session (OOPIF) query frame is never legitimately empty:
                    // an unreadable height there must reject the capture, not pass as expanded.
                    if (session is not null) { AppLog.Write($"扩展框架 {frameId} 失败：子 session frame 内容高度为 {frameContent}。"); failed.Add(frameId); }
                    else AppLog.Write($"扩展框架 {frameId} 跳过：内容高度为 {frameContent}。");
                    continue;
                }

                var objectId = await GetFrameOwnerObjectIdAsync(frameId, token);
                if (string.IsNullOrEmpty(objectId)) { AppLog.Write($"扩展框架 {frameId} 失败：找不到 owner 元素（session={session ?? "-"}）。"); failed.Add(frameId); continue; }
                var ownerSession = OwnerSessionForFrame(frameId);
                var beforeJson = await CallFunctionOnAsync(objectId, ReadFrameGeometryFunction, null, token, ownerSession);
                if (!TryReadFrameGeometry(beforeJson, out var beforeVisible, out var beforeMax, out var beforeBottom, out var beforeRight, out var beforeLeft)) { AppLog.Write($"扩展框架 {frameId} 失败：前置几何读取失败（{beforeJson}）。"); failed.Add(frameId); continue; }
                AppLog.Write($"扩展框架 {frameId}：内容高 {frameContent}，前置可视 {beforeVisible}/{beforeMax}，范围右/下 {beforeRight}/{beforeBottom}。");
                // Already fully exposed: nothing to change and nothing to restore. An attached
                // child-session frame still participates in the capture, though, and if its box
                // extends past the real viewport the capture must enlarge the viewport even
                // without any owner mutation (otherwise the OOPIF below the fold stays blank).
                if (beforeVisible >= frameContent - 2 && beforeMax >= frameContent - 2)
                {
                    if (session is not null)
                        capturedOopif.Add(new OopifPaintRange(frameId, beforeLeft, beforeBottom, beforeRight, contextId.Value, session));
                    continue;
                }

                // Register before mutation so restore runs even if the response is lost.
                expanded.Add(frameId);
                await CallFunctionOnAsync(objectId, GrowFrameFunction, new { value = frameContent }, token, ownerSession);
                await Task.Delay(120, token);

                var afterJson = await CallFunctionOnAsync(objectId, ReadFrameGeometryFunction, null, token, ownerSession);
                if (!TryReadFrameGeometry(afterJson, out var afterVisible, out var afterMax, out var afterBottom, out var afterRight, out var afterLeft)) { AppLog.Write($"扩展框架 {frameId} 失败：后置几何读取失败（{afterJson}）。"); failed.Add(frameId); continue; }
                // A grown attached child-session frame is an OOPIF; captureBeyondViewport does
                // not repaint those beyond the real viewport, so the caller enlarges it first.
                if (session is not null)
                    capturedOopif.Add(new OopifPaintRange(frameId, afterLeft, afterBottom, afterRight, contextId.Value, session));
                var viewportJson = await EvaluateContextAsync(
                    "JSON.stringify({content:Math.max(document.documentElement?document.documentElement.scrollHeight:0,document.body?document.body.scrollHeight:0),viewport:window.innerHeight||0})",
                    contextId, session, token, awaitPromise: false);
                int afterContent, afterViewport;
                try
                {
                    using var document = JsonDocument.Parse(viewportJson);
                    afterContent = (int)Math.Ceiling(document.RootElement.GetProperty("content").GetDouble());
                    afterViewport = (int)Math.Ceiling(document.RootElement.GetProperty("viewport").GetDouble());
                }
                catch { AppLog.Write($"扩展框架 {frameId} 失败：后置内容读取失败（{viewportJson}）。"); failed.Add(frameId); continue; }

                var elementExposed = afterVisible >= Math.Max(frameContent, afterContent) - 2 && afterMax >= Math.Max(frameContent, afterContent) - 2;
                var frameExposed = afterViewport >= afterContent - 2;
                if (!elementExposed || !frameExposed) AppLog.Write($"扩展框架 {frameId} 失败：元素 {afterVisible}/{afterMax}，内容 {afterContent}，视口 {afterViewport}。");
                if (!elementExposed || !frameExposed) failed.Add(frameId);
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
        foreach (var frameId in frameIds.Reverse())
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var objectId = await GetFrameOwnerObjectIdAsync(frameId, token);
                if (string.IsNullOrEmpty(objectId)) continue;
                await CallFunctionOnAsync(objectId, RestoreFrameFunction, null, token, OwnerSessionForFrame(frameId));
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

    /// <summary>
    /// E2E diagnostics: every registered execution context with its frame, CDP session,
    /// authorization and whether the click monitor reached it. Used only to explain an
    /// OOPIF failure; it never relaxes a check.
    /// </summary>
    public async Task<string> DescribeFramesForTestAsync(CancellationToken token)
    {
        List<FrameContext> contexts;
        lock (_frameContexts) contexts = _frameContexts.ToList();
        var parts = new List<string>();
        foreach (var context in contexts)
        {
            string? url;
            lock (_frameUrls) _frameUrls.TryGetValue(context.FrameId, out url);
            string monitor;
            try
            {
                monitor = await SafeEvaluateAsync(
                    "JSON.stringify({monitor:!!window.__customsConsoleMonitor,bound:!!(window.__customsConsoleMonitor&&window.__customsConsoleMonitor.document===document),queryAt:(window.__customsConsoleMonitor&&window.__customsConsoleMonitor.queryAt)||0,href:location.href,ready:document.readyState})",
                    context.ContextId, context.SessionId, token);
            }
            catch (Exception ex) { monitor = "error:" + ex.Message; }
            parts.Add($"frame={context.FrameId};session={context.SessionId ?? "-"};authorized={IsAuthorizedFrameId(context.FrameId)};url={url};monitor={monitor}");
        }
        lock (_frameTargetSessions)
            foreach (var pair in _frameTargetSessions) parts.Add($"target={pair.Key};session={pair.Value}");
        return parts.Count == 0 ? "(未登记任何执行上下文)" : string.Join(" | ", parts);
    }

    /// <summary>Top-level frame id owned by the root CDP session (E2E identity evidence).</summary>
    public string MainFrameIdForTest
    {
        get { lock (_frameUrls) return _mainFrameId; }
    }

    /// <summary>Top-level page URL owned by the root CDP session (E2E identity evidence).</summary>
    public string CurrentUrlForTest
    {
        get { lock (_frameUrls) return _currentUrl; }
    }

    /// <summary>Top-level navigation generation, only advanced by a real main-frame navigation.</summary>
    public int NavigationGenerationForTest => Volatile.Read(ref _navigationGeneration);

    /// <summary>Snapshot of every registered frame id and its last known URL.</summary>
    public IReadOnlyDictionary<string, string> FrameUrlsForTest()
    {
        lock (_frameUrls) return new Dictionary<string, string>(_frameUrls);
    }

    /// <summary>The registered parent link for a frame, or null when no parent is known.</summary>
    public string? FrameParentForTest(string frameId)
    {
        lock (_frameParents) return _frameParents.TryGetValue(frameId, out var parent) ? parent : null;
    }

    /// <summary>
    /// Navigates a flattened child target's own document through its child CDP session. This
    /// is the exact event path where Page.frameNavigated arrives with no parentId and must not
    /// be mistaken for a top-level navigation (E2E regression helper).
    /// </summary>
    public async Task<bool> NavigateChildFrameForTestAsync(string frameId, string url, CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        string? session;
        lock (_frameTargetSessions) _frameTargetSessions.TryGetValue(frameId, out session);
        if (string.IsNullOrEmpty(session)) return false;
        await cdp.SendAsync("Page.navigate", new { url }, token, session);
        return true;
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
        LastClickFailure = "";
        LastClickExactHit = "";
        LastClickPointer = "";
        // The card is hidden and shown around every capture. A CDP click dispatched before the
        // card is painted again can be lost (a compositor/paint risk to verify, not a proven
        // cause), so the single click is gated on a bounded render-readiness condition instead
        // of a blind sleep or an automatic second click. The first ready probe fixes the
        // baseline viewport every later click must see restored.
        try
        {
            LastClickReadiness = await WaitForWidgetInteractiveAsync(TimeSpan.FromSeconds(3), _clickBaselineViewport, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastClickReadiness = "就绪探测失败:" + ex.Message;
        }
        if (!TryParseReadiness(LastClickReadiness, out var readiness))
            return FailClick($"无法解析卡片就绪诊断：{Shorten(LastClickReadiness)}");
        // Dispatch nothing unless the snapshot is a valid ready=true that also proves the button
        // itself (not just the host card) is enabled, visible and exactly under the point. A
        // readiness timeout or a not-ready/exact-miss snapshot returns false with the
        // diagnostic instead of sending a mouse event the page cannot deliver.
        if (!readiness.Ready || !readiness.ExactButtonHit || !readiness.Enabled || !readiness.Visible)
            return FailClick($"卡片未就绪，未发送真实点击：{Shorten(LastClickReadiness)}");
        if (_clickBaselineViewport is null && readiness.Viewport is { } baseline)
            _clickBaselineViewport = baseline;
        var rectJson = await EvaluateContextAsync(
            "JSON.stringify((window.__cccWidget && window.__cccWidget.captureButtonRect && window.__cccWidget.captureButtonRect()) || null)",
            null, token, awaitPromise: false);
        if (string.IsNullOrWhiteSpace(rectJson) || rectJson.TrimStart().StartsWith("null", StringComparison.OrdinalIgnoreCase))
            return FailClick("未找到可见的卡片按钮矩形。");
        double x, y;
        try
        {
            using var document = JsonDocument.Parse(rectJson);
            var root = document.RootElement;
            x = root.GetProperty("x").GetDouble() + root.GetProperty("width").GetDouble() / 2;
            y = root.GetProperty("y").GetDouble() + root.GetProperty("height").GetDouble() / 2;
        }
        catch (Exception ex) { return FailClick($"读取卡片按钮位置失败：{ex.Message}"); }
        var point = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x},{y}");
        // Re-check the exact point once more after taking the rect and immediately before the
        // trusted mouse input: a readiness snapshot that has gone stale must not authorise a
        // changed/hidden point. The closed-shadow hit is the exact evidence; top host is kept.
        LastClickExactHit = await EvaluateContextAsync(
            $"JSON.stringify((window.__cccWidget && window.__cccWidget.hitTest && window.__cccWidget.hitTest({point})) || null)",
            null, token, awaitPromise: false);
        if (!TryParseExactButtonHit(LastClickExactHit, out var exactButtonHit, out var topIsHost))
            return FailClick($"无法解析发送前精确命中复核：{Shorten(LastClickExactHit)}");
        LastClickHitTarget = topIsHost ? "host" : "none";
        if (!exactButtonHit || !topIsHost)
            return FailClick($"发送前精确命中复核失败（exact={exactButtonHit},host={topIsHost}）：{Shorten(LastClickExactHit)}");
        // The page's own hit test runs on fresh layout, but the browser routes real input by the
        // compositor's hit-test data, which can still describe the frame that was grown during
        // the previous capture. A click sent then lands in that frame and is silently lost. So the
        // real mouse is moved first and only pressed once the button itself has received the
        // move; a move is not a click, and exactly one press/release is sent.
        var routed = await WaitForPointerRoutedAsync(cdp, x, y, token);
        if (routed is null)
            return FailClick($"真实指针移动未到达卡片按钮，未发送按下：{Shorten(LastClickPointer)}");
        var (clickX, clickY) = routed.Value;
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x = clickX, y = clickY, button = "left", clickCount = 1 }, token);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = clickX, y = clickY, button = "left", clickCount = 1 }, token);
        return true;
    }

    /// <summary>Evidence of the pointer-routing handshake before the last real card click.</summary>
    public string LastClickPointer { get; private set; } = "";

    private async Task<long> ReadPointerMovesAsync(CancellationToken token)
    {
        var value = await EvaluateContextAsync(
            "String((window.__cccWidget && window.__cccWidget.diagnostics) ? window.__cccWidget.diagnostics().pointerMoveCount : -1)",
            null, token, awaitPromise: false);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : -1;
    }

    /// <summary>
    /// Moves the real mouse inside the button (a small alternating offset so every move is a
    /// genuine position change) until the button reports a routed pointermove, bounded to a
    /// few seconds. Returns the point that was confirmed, or null.
    /// </summary>
    private async Task<(double X, double Y)?> WaitForPointerRoutedAsync(CdpClient cdp, double x, double y, CancellationToken token)
    {
        var baseline = await ReadPointerMovesAsync(token);
        if (baseline < 0) { LastClickPointer = "卡片不支持指针计数"; return null; }
        var started = DateTime.UtcNow;
        for (var attempt = 0; DateTime.UtcNow - started < TimeSpan.FromSeconds(5); attempt++)
        {
            var offset = attempt % 2 == 0 ? -2.0 : 2.0;
            var pointX = x + offset;
            await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = pointX, y, button = "none", clickCount = 0 }, token);
            var waitStarted = DateTime.UtcNow;
            while (DateTime.UtcNow - waitStarted < TimeSpan.FromMilliseconds(200))
            {
                var moves = await ReadPointerMovesAsync(token);
                if (moves > baseline)
                {
                    LastClickPointer = FormattableString.Invariant($"moves={attempt + 1};routed={moves - baseline}");
                    return (pointX, y);
                }
                await Task.Delay(25, token);
            }
        }
        LastClickPointer = FormattableString.Invariant($"5 秒内指针移动未路由到按钮；基线={baseline}");
        return null;
    }

    private bool FailClick(string reason)
    {
        LastClickFailure = reason;
        AppLog.Write($"真实点击未执行：{reason}");
        return false;
    }

    private static string Shorten(string value) => value.Length > 400 ? value[..400] : value;

    /// <summary>Navigates the page (E2E helper for leave/return and reload flows).</summary>
    public async Task NavigateAsync(string url, CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        await cdp.SendAsync("Page.navigate", new { url }, token);
    }

    /// <summary>
    /// Clicks an element inside a direct child iframe with real CDP mouse input. The
    /// element's own rect is found in whichever authorized frame contains it, then offset
    /// by the iframe element's rect in the top page. Used to trigger the official query
    /// button like a user, not through page script.
    /// </summary>
    public async Task<bool> ClickElementInFrameAsync(string frameSelector, string elementSelector, CancellationToken token)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        var selector = JsonSerializer.Serialize(elementSelector);
        var rectValue = "";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            rectValue = await EvaluateAllContextsAsync(
                $"(() => {{ const el = document.querySelector({selector}); if (!el) return ''; const r = el.getBoundingClientRect(); return 'rect|' + r.left + ',' + r.top + ',' + r.width + ',' + r.height; }})()",
                token, false, "rect|");
            if (rectValue.StartsWith("rect|", StringComparison.Ordinal)) break;
            await Task.Delay(250, token);
        }
        if (!rectValue.StartsWith("rect|", StringComparison.Ordinal)) return false;
        var parts = rectValue[5..].Split(',');
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var top) ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height)) return false;
        var frameJson = await EvaluateContextAsync(
            $"JSON.stringify((function(){{ var f = document.querySelector({JsonSerializer.Serialize(frameSelector)}); return f ? f.getBoundingClientRect() : null; }})())",
            null, token, awaitPromise: false);
        double frameLeft = 0, frameTop = 0;
        try
        {
            using var document = JsonDocument.Parse(frameJson);
            frameLeft = document.RootElement.GetProperty("left").GetDouble();
            frameTop = document.RootElement.GetProperty("top").GetDouble();
        }
        catch (Exception ex) { AppLog.Write($"读取 iframe 位置失败：{ex.Message}"); return false; }
        var x = frameLeft + left + width / 2;
        var y = frameTop + top + height / 2;
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none", clickCount = 0 }, token);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, token);
        await cdp.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, token);
        return true;
    }

    /// <summary>Aborts the control socket so the reconnect production path can be exercised.</summary>
    public void SimulateConnectionDropForTest() => _cdp?.AbortForTest();

    /// <summary>
    /// E2E-only fault injection: makes the next <paramref name="count"/> viewport reads fail so
    /// the required reject-on-unreadable behavior for an OOPIF capture is exercised without a
    /// real browser fault. Normal runs never set this.
    /// </summary>
    public void FailNextViewportReadsForTest(int count) => _viewportReadFailures = Math.Max(0, count);

    public Task<string> CurrentUrlAsync(CancellationToken token) =>
        EvaluateContextAsync("location.href", null, token, awaitPromise: false);

    public Task<bool> CanCaptureAsync(CancellationToken token) =>
        EvaluateBooleanAsync("!!(window.__cccWidget && window.__cccWidget.canCapture())", token);

    /// <summary>
    /// E2E contract hook: whether the single-owner capture gate is currently held. The
    /// production completion event must never be observed while this is true, so a fast retry
    /// can always run once the page shows a retryable button.
    /// </summary>
    public bool CaptureLockHeldForTest => Volatile.Read(ref _captureGate) != 0;

    /// <summary>Render-readiness evidence captured immediately before the last real card click.</summary>
    public string LastClickReadiness { get; private set; } = "";

    /// <summary>Exact closed-shadow button hit evidence for the last real card click.</summary>
    public string LastClickExactHit { get; private set; } = "";

    /// <summary>Why the last real card click was not dispatched, or empty when it was.</summary>
    public string LastClickFailure { get; private set; } = "";

    /// <summary>
    /// Render-readiness observed just before the last production completion event was published.
    /// This is a separate recovery/UX signal: it never changes the saved-file verdict.
    /// </summary>
    public string LastCompletionReadiness { get; private set; } = "";

    /// <summary>
    /// E2E diagnostics for the card's input chain: binding availability, card state, exact
    /// closed-shadow button rect, viewport/DPR and the closure's DOM-click and binding-call
    /// counters. Every real-click/timeout branch records this so a lost click is localised
    /// (harness hit-test, page handler or CDP binding) instead of guessed.
    /// </summary>
    public Task<string> DescribeWidgetForTestAsync(CancellationToken token) =>
        EvaluateRawAsync(
            "JSON.stringify((window.__cccWidget && window.__cccWidget.diagnostics) ? window.__cccWidget.diagnostics() : null)",
            token);

    /// <summary>
    /// Bounded render-readiness gate (never a blind sleep): resolves once the card is enabled
    /// and visible with a rect stable for two animation frames and an exact closed-shadow button
    /// hit; when <paramref name="expectedViewport"/> is given the temporarily enlarged capture
    /// viewport must first be restored. Returns the JSON diagnostics snapshot.
    /// </summary>
    public Task<string> WaitForWidgetInteractiveAsync(TimeSpan timeout, (int Width, int Height, double Scale)? expectedViewport, CancellationToken token)
    {
        var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, 30_000);
        var expected = expectedViewport is { } viewport
            ? FormattableString.Invariant($"{{w:{viewport.Width},h:{viewport.Height},dpr:{viewport.Scale}}}")
            : "null";
        return EvaluateContextAsync(
            $"window.__cccWidget && window.__cccWidget.whenInteractive ? window.__cccWidget.whenInteractive({milliseconds}, {expected}).then(function(r){{ return JSON.stringify(r); }}) : Promise.resolve('')",
            null, token, awaitPromise: true);
    }

    /// <summary>
    /// The decision fields parsed out of a whenInteractive diagnostics snapshot. Parsing is
    /// strict: a snapshot that is missing ready/hit/enabled/visible is not accepted as ready.
    /// </summary>
    private sealed record ClickReadinessSnapshot(
        bool Ready, string Reason, bool ExactButtonHit, bool Enabled, bool Visible,
        (int Width, int Height, double Scale)? Viewport);

    private static bool TryParseReadiness(string? json, out ClickReadinessSnapshot snapshot)
    {
        snapshot = new ClickReadinessSnapshot(false, "", false, false, false, null);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var ready = root.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() ?? "" : "";
            var enabled = root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            var visible = root.TryGetProperty("visible", out var vi) && vi.ValueKind == JsonValueKind.True;
            var exact = root.TryGetProperty("hit", out var hit) && hit.ValueKind == JsonValueKind.Object
                && hit.TryGetProperty("exactButtonHit", out var eb) && eb.ValueKind == JsonValueKind.True;
            (int Width, int Height, double Scale)? viewport = null;
            if (root.TryGetProperty("viewport", out var v) && v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Number &&
                v.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Number &&
                v.TryGetProperty("dpr", out var dpr) && dpr.ValueKind == JsonValueKind.Number)
            {
                var width = w.GetInt32();
                var height = h.GetInt32();
                var scale = dpr.GetDouble();
                if (width > 0 && height > 0 && scale > 0) viewport = (width, height, scale);
            }
            snapshot = new ClickReadinessSnapshot(ready, reason, exact, enabled, visible, viewport);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Parses the exact closed-shadow button hit evidence returned by widget.hitTest.</summary>
    private static bool TryParseExactButtonHit(string? json, out bool exactButtonHit, out bool topIsHost)
    {
        exactButtonHit = false;
        topIsHost = false;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            exactButtonHit = root.TryGetProperty("exactButtonHit", out var e) && e.ValueKind == JsonValueKind.True;
            topIsHost = root.TryGetProperty("topIsHost", out var t) && t.ValueKind == JsonValueKind.True;
            return true;
        }
        catch (JsonException) { return false; }
    }

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

    private Task<string> EvaluateContextAsync(string expression, int? contextId, CancellationToken token, bool awaitPromise) =>
        EvaluateContextAsync(expression, contextId, null, token, awaitPromise);

    private async Task<string> EvaluateContextAsync(string expression, int? contextId, string? sessionId, CancellationToken token, bool awaitPromise)
    {
        var cdp = _cdp ?? throw new InvalidOperationException("浏览器控制连接已断开。");
        var parameters = contextId is null
            ? (object)new { expression, returnByValue = true, awaitPromise }
            : new { expression, contextId = contextId.Value, returnByValue = true, awaitPromise };
        var response = await cdp.SendAsync("Runtime.evaluate", parameters, token, sessionId);
        // A page script exception is a normal response payload, not a protocol error; it must
        // be surfaced so a failed prepare/verify is never mistaken for an empty success.
        if (response.TryGetProperty("result", out var envelope) && envelope.TryGetProperty("exceptionDetails", out var exception))
            throw new InvalidOperationException($"网页脚本异常：{exception}");
        try
        {
            var result = response.GetProperty("result").GetProperty("result");
            return result.TryGetProperty("value", out var value) ? value.ToString() : "";
        }
        catch { return ""; }
    }

    private async Task<string> SafeEvaluateAsync(string expression, int? contextId, string? sessionId, CancellationToken token)
    {
        try { return await EvaluateContextAsync(expression, contextId, sessionId, token, awaitPromise: true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { return ""; }
    }

    /// <summary>Ranking used only when no context produced a terminal marker.</summary>
    private static int MarkerPriority(string value)
    {
        if (value.Contains("stable|", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("ready|", StringComparison.OrdinalIgnoreCase)) return 6;
        if (value.Contains("mismatch|", StringComparison.OrdinalIgnoreCase)) return 5;
        if (value.Contains("error|", StringComparison.OrdinalIgnoreCase)) return 4;
        if (value.Contains("stale|", StringComparison.OrdinalIgnoreCase)) return 3;
        if (value.Contains("loading|", StringComparison.OrdinalIgnoreCase)) return 2;
        if (value.Contains("waiting|", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    /// <summary>
    /// Evaluates an expression in the top-level shell and in every authorized frame context.
    /// A terminal marker in ANY frame wins; "waiting" from the outer shell does not stop the
    /// search for a ready iframe. When nothing is terminal the strongest pending value is
    /// returned so the caller can still refuse correctly.
    /// </summary>
    private async Task<string> EvaluateAllContextsAsync(string expression, CancellationToken token, bool childrenFirst, params string[] acceptedMarkers)
    {
        if (_cdp is null) return "";
        if (acceptedMarkers.Length == 0) acceptedMarkers = ["ready|", "mismatch|", "error|", "loading|", "stale|", "prepared:", "restored:", "nothing-to-restore"];
        static bool Accepted(string value, IReadOnlyCollection<string> markers) =>
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var results = new List<string>();
        var main = await SafeEvaluateAsync(expression, null, null, token);
        results.Add(main);
        if (Accepted(main, acceptedMarkers)) return main;

        await RefreshUnknownFrameUrlsAsync(token);
        var contexts = AuthorizedContexts();
        var ordered = childrenFirst
            ? contexts.OrderBy(x => x.FrameId.Equals(_mainFrameId, StringComparison.Ordinal) ? 1 : 0).ToList()
            : contexts.OrderBy(x => x.FrameId.Equals(_mainFrameId, StringComparison.Ordinal) ? 0 : 1).ToList();
        foreach (var context in ordered)
        {
            token.ThrowIfCancellationRequested();
            var value = await SafeEvaluateAsync(expression, context.ContextId, context.SessionId, token);
            if (Accepted(value, acceptedMarkers)) return value;
            results.Add(value);
        }
        return results.OrderByDescending(MarkerPriority).ThenByDescending(x => x.Length).FirstOrDefault() ?? main;
    }

    private async Task EvaluateAllContextsAsync(string expression, CancellationToken token, bool childrenFirst, bool bestEffort)
    {
        if (_cdp is null) return;
        await RefreshUnknownFrameUrlsAsync(token);
        var contexts = AuthorizedContexts();
        var ordered = childrenFirst
            ? contexts.OrderBy(x => x.FrameId.Equals(_mainFrameId, StringComparison.Ordinal) ? 1 : 0).ToList()
            : contexts.OrderBy(x => x.FrameId.Equals(_mainFrameId, StringComparison.Ordinal) ? 0 : 1).ToList();
        foreach (var context in ordered)
        {
            if (token.IsCancellationRequested) break;
            try { await EvaluateContextAsync(expression, context.ContextId, context.SessionId, token, awaitPromise: true); }
            catch (Exception ex) { if (!bestEffort) throw; AppLog.Write($"网页状态准备/恢复失败：{ex.Message}"); }
        }
        try { await EvaluateContextAsync(expression, null, null, token, awaitPromise: true); }
        catch (Exception ex) { if (!bestEffort) throw; AppLog.Write($"网页状态准备/恢复失败：{ex.Message}"); }
    }

    private async Task<string> EvaluateInAllFramesAsync(string expression, CancellationToken token, params string[] acceptedMarkers)
    {
        if (_cdp is null) return "";
        if (acceptedMarkers.Length == 0) acceptedMarkers = ["filled"];
        static bool Accepted(string value, IReadOnlyCollection<string> markers) =>
            markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var main = await SafeEvaluateAsync(expression, null, null, token);
        if (Accepted(main, acceptedMarkers)) return main;

        var targets = await AuthorizedEvalTargetsAsync(token);
        foreach (var target in targets)
        {
            if (target.FrameId.Equals(_mainFrameId, StringComparison.Ordinal) && target.ContextId is null) continue;
            try
            {
                var value = await EvaluateTargetAsync(expression, target, token, awaitPromise: true);
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
        return reader.ReadToEnd()
            .Replace("__DECLARATION_NO__", JsonSerializer.Serialize(declarationNo))
            .Replace("__RESULT_SELECTORS__", JsonSerializer.Serialize(ResultSelectors));
    }

    /// <summary>
    /// The single result-region definition shared by identity/verify/probe. It covers the
    /// official #queryDetail .display-content .content-field renderer (R6) as well as the
    /// table/timeline shapes used by the controlled fixtures.
    /// </summary>
    private const string ResultSelectors =
        "table tbody tr,.timeline li,.el-timeline-item,.ant-timeline-item,.query-result tr,.result-list li," +
        "[class*=result] tbody tr,[class*=inquiry] tbody tr,[class*=detail] tbody tr," +
        "#queryDetail .display-content .content-field,#queryDetail .content-field,[class*=display-content] .content-field," +
        "#queryDetail [class*=field-order],[class*=display-content] [class*=field-order]";

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
        if (_process is not null)
        {
            _process.Exited -= OnBrowserExited;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
            _process.Dispose();
        }
        _lifetime.Dispose();
        if (_profileFolder is not null) await DeleteProfileAsync(_profileFolder);
    }

    /// <summary>Best-effort removal of a session profile; browser helpers can hold files briefly after exit.</summary>
    private static async Task DeleteProfileAsync(string folder)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) { AppLog.Write($"浏览器临时配置暂未删除（下次启动时清理）：{ex.Message}"); return; }
            }
            await Task.Delay(400);
        }
    }

    /// <summary>Deletes session profiles older than <paramref name="age"/>; a profile still in use fails to delete and is kept.</summary>
    internal static void PurgeStaleProfiles(string root, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < cutoff) Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) { AppLog.Write($"清理浏览器临时配置失败：{ex.Message}"); }
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

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(90);

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

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken token, string? sessionId = null)
    {
        if (_closed) throw new IOException("浏览器控制连接已关闭。");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        // Flattened OOPIF sessions are addressed with a top-level sessionId; commands for
        // the page session omit the field entirely.
        var envelope = sessionId is null
            ? (object)new { id, method, @params = parameters ?? new { } }
            : new { id, method, @params = parameters ?? new { }, sessionId };
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        try
        {
            await _sendLock.WaitAsync(token);
            try { await _socket.SendAsync(payload, WebSocketMessageType.Text, true, token); }
            finally { _sendLock.Release(); }
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            // A renderer that hangs without closing the socket would otherwise leave the caller
            // (and a running capture's single-owner gate) waiting forever. Generous bound: the
            // slowest legitimate command is a 12000px screenshot tile.
            return await completion.Task.WaitAsync(CommandTimeout, token);
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
