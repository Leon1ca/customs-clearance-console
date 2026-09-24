using System.Text;
using System.Text.Json;

namespace CustomsClearanceConsole;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.ShowUnexpected(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write(e.ExceptionObject as Exception ?? new Exception("未知错误"));

        AppFonts.Initialize();
        TryInitializePdfium();

        if (args.Length >= 2 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--regression-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunKnownRegressionAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--ocr-debug", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.DumpOcrAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--ui-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var width = args.Length >= 3 && int.TryParse(args[2], out var parsedWidth) ? parsedWidth : 1440;
            var height = args.Length >= 4 && int.TryParse(args[3], out var parsedHeight) ? parsedHeight : 900;
            SelfTest.CaptureUi(args[1], width, height);
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--ui-state-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.CaptureAllStates(args[1]);
            return;
        }
        if (args.Length >= 3 && args[0].Equals("--ui-dialog-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.CaptureDialog(args[1], args[2]);
            return;
        }
        if (args.Length >= 1 && args[0].Equals("--ui-contract-self-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.RunUiContracts();
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--export-samples", StringComparison.OrdinalIgnoreCase))
        {
            var report = ExportValidation.Run(args[1]);
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = report.Pass ? 0 : 1;
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--browser-e2e", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = BrowserCaptureE2E.RunAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--env-report", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.WriteEnvironmentReport(args[1]);
            return;
        }
        Application.Run(new MainForm());
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
