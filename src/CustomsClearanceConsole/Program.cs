using System.Text;
using System.Text.Json;

namespace CustomsClearanceConsole;

internal static class Program
{
    /// <summary>
    /// Every headless test/report command. These run unattended on CI; an unexpected UI-thread
    /// exception must terminate them immediately instead of opening a modal dialog that nobody
    /// can dismiss. Keep this list in sync with the dispatch below.
    /// </summary>
    private static readonly HashSet<string> CommandLineModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "--self-test",
        "--regression-test",
        "--ocr-debug",
        "--ui-snapshot",
        "--ui-state-snapshot",
        "--ui-dialog-snapshot",
        "--ui-contract-self-test",
        "--export-samples",
        "--browser-e2e",
        "--env-report"
    };

    [STAThread]
    private static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();

        var commandLineMode = args.Length >= 1 && CommandLineModes.Contains(args[0]);
        if (commandLineMode)
        {
            // CI test/report commands must fail fast. The default handler
            // (AppLog.ShowUnexpected) shows a synchronous MessageBox, which hangs an
            // unattended snapshot run until the whole job times out. Write the full
            // exception to stderr and app.log, then exit non-zero straight away.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => FailCommandLine("UI 线程未处理异常", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                FailCommandLine("进程未处理异常", e.ExceptionObject as Exception ?? new Exception("未知错误"));
        }
        else
        {
            // Normal desktop use keeps the friendly interactive error dialog.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => AppLog.ShowUnexpected(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLog.Write(e.ExceptionObject as Exception ?? new Exception("未知错误"));
        }

        // Fire-and-forget work (browser disposal, background cleanup) must not fail silently.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write(e.Exception);
            e.SetObserved();
        };

        AppFonts.Initialize();
        TryInitializePdfium();

        try
        {
            if (RunCommand(args)) return;
        }
        catch (Exception ex) when (commandLineMode)
        {
            FailCommandLine($"命令 {args[0]} 失败", ex);
        }

        // OCR work folders are removed after each file; a crash or power loss can leave rendered
        // pages behind. Only folders older than a day are swept, never a running scan's.
        _ = Task.Run(() => TemporaryDirectory.PurgeStale(AppPaths.TempRoot, TimeSpan.FromDays(1)));
        Application.Run(new MainForm());
    }

    /// <summary>Runs a headless command; returns false when the app should start the GUI.</summary>
    private static bool RunCommand(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--regression-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunKnownRegressionAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--ocr-self-test", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = OcrSelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--ocr-debug", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.DumpOcrAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--ui-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var width = args.Length >= 3 && int.TryParse(args[2], out var parsedWidth) ? parsedWidth : 1440;
            var height = args.Length >= 4 && int.TryParse(args[3], out var parsedHeight) ? parsedHeight : 900;
            SelfTest.CaptureUi(args[1], width, height);
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--ui-state-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.CaptureAllStates(args[1]);
            return true;
        }
        if (args.Length >= 3 && args[0].Equals("--ui-dialog-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.CaptureDialog(args[1], args[2]);
            return true;
        }
        if (args.Length >= 1 && args[0].Equals("--ui-contract-self-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunUiContracts();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--export-samples", StringComparison.OrdinalIgnoreCase))
        {
            var report = ExportValidation.Run(args[1]);
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = report.Pass ? 0 : 1;
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--browser-e2e", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = BrowserCaptureE2E.RunAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--env-report", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.WriteEnvironmentReport(args[1]);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Headless fatal path: persist the full exception, mirror it to stderr (captured in the
    /// CI job log), then terminate with a non-zero exit code. Never shows a dialog.
    /// </summary>
    private static void FailCommandLine(string context, Exception ex)
    {
        var detail = $"[CLI-FATAL] {context}: {Environment.NewLine}{ex}";
        try { AppLog.Write(detail); } catch { }
        try
        {
            Console.Error.WriteLine(detail);
            Console.Error.WriteLine($"[CLI-FATAL] AppLog: {AppLog.FilePath}");
            Console.Error.Flush();
            Console.Out.Flush();
        }
        catch { }
        Environment.Exit(1);
    }

    private static void TryInitializePdfium()
    {
        try { PdfiumNative.Initialize(); }
        catch (Exception ex)
        {
            // UI, export and browser-only commands must run even without the PDF runtime;
            // OCR commands surface the missing dependency when they actually extract.
            AppLog.Write($"PDF 组件初始化未完成：{ex.Message}");
        }
    }
}
