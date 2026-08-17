namespace CustomsClearanceConsole;

internal sealed class BatchScanner
{
    public const int MaximumFiles = 200;
    public static readonly string[] SupportedExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];
    private readonly DocumentExtractor _extractor = new();
    private readonly DeclarationParser _parser = new();

    public async Task<List<DeclarationRecord>> ScanAsync(string folder, IProgress<(int Done, int Total, string File)> progress, CancellationToken cancellationToken)
    {
        var extensions = new HashSet<string>(SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(x => extensions.Contains(Path.GetExtension(x)))
            .OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (files.Count == 0) throw new InvalidOperationException("所选文件夹中没有支持的关单文件。支持 PDF、PNG、JPG、BMP、TIF/TIFF。 ");
        if (files.Count > MaximumFiles) throw new InvalidOperationException($"当前文件夹有 {files.Count} 个支持的文件，超过每批 {MaximumFiles} 个的上限。请拆分文件夹后再识别。");

        var result = new List<DeclarationRecord>();
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((i, files.Count, Path.GetFileName(files[i])));
            try
            {
                var text = await _extractor.ExtractAsync(files[i], cancellationToken);
                result.Add(_parser.Parse(files[i], text));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Write($"识别失败：{files[i]}\n{ex}");
                result.Add(new DeclarationRecord
                {
                    SourcePath = files[i], Status = "识别失败", Warning = ex.Message, Confidence = 0
                });
            }
        }
        MarkDuplicates(result);
        progress.Report((files.Count, files.Count, "识别完成"));
        return SortRecords(result).ToList();
    }

    public static void MarkDuplicates(List<DeclarationRecord> records)
    {
        foreach (var record in records) { record.IsDuplicate = false; record.IsCanonical = true; }
        foreach (var group in records.Where(x => x.DeclarationNo.Length == 18).GroupBy(x => x.DeclarationNo).Where(x => x.Count() > 1))
        {
            var canonical = group.OrderByDescending(x => x.Confidence).ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase).First();
            foreach (var record in group)
            {
                var recognitionWarning = record.Status == "需关注" ? record.Warning : "";
                record.IsDuplicate = true;
                record.IsCanonical = ReferenceEquals(record, canonical);
                record.Status = "重复单号";
                var valuesDiffer = !SameContents(record, canonical);
                var duplicateWarning = valuesDiffer
                    ? "同一报关单号的识别内容不一致；去重合计采用完整度更高的一条"
                    : "当前批次存在相同报关单号；去重合计仅计一次";
                record.Warning = string.IsNullOrWhiteSpace(recognitionWarning)
                    ? duplicateWarning
                    : $"{duplicateWarning}；{recognitionWarning}";
            }
        }
    }

    private static bool SameContents(DeclarationRecord a, DeclarationRecord b) =>
        a.Consignee == b.Consignee && a.ContractNo == b.ContractNo && a.ExitCustoms == b.ExitCustoms &&
        a.DestinationCountry == b.DestinationCountry && a.Totals.Count == b.Totals.Count &&
        a.Totals.All(x => b.Totals.TryGetValue(x.Key, out var value) && value == x.Value);

    public static IEnumerable<DeclarationRecord> SortRecords(IEnumerable<DeclarationRecord> records) =>
        records.OrderByDescending(x => x.IsDuplicate)
            .ThenBy(x => x.DeclarationNo.Length == 0)
            .ThenBy(x => x.DeclarationNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, decimal> GrossTotals(IEnumerable<DeclarationRecord> records) => Sum(records);
    public static Dictionary<string, decimal> DeduplicatedTotals(IEnumerable<DeclarationRecord> records) => Sum(records.Where(x => x.IsCanonical));

    private static Dictionary<string, decimal> Sum(IEnumerable<DeclarationRecord> records)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in records.SelectMany(x => x.Totals)) result[pair.Key] = result.GetValueOrDefault(pair.Key) + pair.Value;
        return result;
    }
}
