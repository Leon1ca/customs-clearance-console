using System.Text;

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

        PdfiumNative.Initialize();

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
            var width = args.Length >= 3 && int.TryParse(args[2], out var parsedWidth) ? parsedWidth : 1365;
            var height = args.Length >= 4 && int.TryParse(args[3], out var parsedHeight) ? parsedHeight : 768;
            SelfTest.CaptureUi(args[1], width, height);
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
        if (args.Length >= 2 && args[0].Equals("--browser-smoke", StringComparison.OrdinalIgnoreCase))
        {
            BrowserSmokeTest.RunAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        Application.Run(new MainForm());
    }
}

internal static class BrowserSmokeTest
{
    public static async Task RunAsync(string outputFolder)
    {
        await using var browser = new BrowserValidation();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var status = await browser.StartAsync("292120260001476464", timeout.Token);
            Console.WriteLine(status);
            Directory.CreateDirectory(outputFolder);
            File.WriteAllText(Path.Combine(outputFolder, "browser-connection.txt"), status);
            Console.WriteLine("BROWSER_CONNECTION_OK · 验证码与真实结果截图需人工验收");
            Environment.ExitCode = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
}
