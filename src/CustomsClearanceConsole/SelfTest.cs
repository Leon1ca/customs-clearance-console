using System.Text.Json;

namespace CustomsClearanceConsole;

internal static class SelfTest
{
    public static void RunUiContracts()
    {
        var copied = MainForm.FormatCellSelection([
            (1, 2, "第二行第二项"),
            (0, 4, "只选这一格"),
            (1, 1, "第二行第一项")
        ]);
        var expected = $"只选这一格{Environment.NewLine}第二行第一项\t第二行第二项";
        if (copied != expected) throw new InvalidOperationException($"单元格复制格式不符合约定：{copied}");
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("UI_CONTRACTS_OK");
    }

    public static void CaptureUi(string outputPath, int width, int height)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var form = new MainForm
        {
            Size = new Size(Math.Max(900, width), Math.Max(650, height)),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }

    public static async Task DumpOcrAsync(string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var document = await new DocumentExtractor().ExtractAsync(path, CancellationToken.None);
        static object Page(TextPage page) => new
        {
            page.Width,
            page.Height,
            tokens = page.Tokens
                .OrderBy(x => x.CenterY)
                .ThenBy(x => x.Left)
                .Select(x => new { x.Text, x.Left, x.Top, x.Right, x.Bottom, x.Confidence })
        };
        var output = new
        {
            primary = document.Pages.Select(Page),
            secondary = document.VerificationPages.Select(Page),
            document.SecondaryOcrError
        };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunAsync(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(x => Console.Error.WriteLine($"{x.Done}/{x.Total} {x.File}"));
            var records = await new BatchScanner().ScanAsync(folder, progress, CancellationToken.None);
            var output = new
            {
                fileCount = records.Count,
                uniqueCount = records.Count(x => x.IsCanonical),
                duplicateGroups = records.Where(x => x.IsDuplicate).GroupBy(x => x.DeclarationNo).Count(),
                grossTotals = BatchScanner.GrossTotals(records),
                deduplicatedTotals = BatchScanner.DeduplicatedTotals(records),
                records = records.Select(x => new { x.SourceName, x.DeclarationNo, x.Consignee, x.ContractNo, x.ExitCustoms, x.DestinationCountry, x.Totals, x.Status, x.Warning, x.Confidence, x.IsDuplicate, x.IsCanonical })
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = records.Any(x => x.Status is "需关注" or "识别失败") ? 2 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
}
