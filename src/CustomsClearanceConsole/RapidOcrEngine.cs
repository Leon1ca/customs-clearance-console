using RapidOCRLib;

namespace CustomsClearanceConsole;

/// <summary>
/// Independent PP-OCRv5/ONNX verifier. It intentionally returns its own token page;
/// callers must parse and reconcile it separately instead of mixing OCR text streams.
/// </summary>
internal sealed class RapidOcrEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrLite? _engine;

    public async Task<TextPage> RecognizeAsync(string imagePath, int width, int height, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var engine = await GetEngineAsync();
            cancellationToken.ThrowIfCancellationRequested();

            const int padding = 40;
            var savedOutput = Console.Out;
            try
            {
                // RapidOCR's library writes diagnostic details to Console.Out. Suppress them so
                // self-test JSON remains machine readable and the GUI build stays quiet.
                Console.SetOut(TextWriter.Null);
                var result = await engine.DetectAsync(
                    imagePath,
                    padding: padding,
                    maxSideLen: 2800,
                    boxScoreThresh: .38f,
                    boxThresh: .25f,
                    unClipRatio: 1.65f,
                    doAngle: true,
                    mostAngle: false);

                var tokens = new List<TextToken>();
                foreach (var block in result.TextBlocks ?? [])
                {
                    if (string.IsNullOrWhiteSpace(block.Text) || block.BoxPoints is null || block.BoxPoints.Count == 0)
                        continue;

                    var left = Math.Clamp(block.BoxPoints.Min(x => x.X) - padding, 0, width);
                    var top = Math.Clamp(block.BoxPoints.Min(x => x.Y) - padding, 0, height);
                    var right = Math.Clamp(block.BoxPoints.Max(x => x.X) - padding, 0, width);
                    var bottom = Math.Clamp(block.BoxPoints.Max(x => x.Y) - padding, 0, height);
                    if (right <= left || bottom <= top) continue;

                    var charConfidence = block.CharScores is { Count: > 0 } ? block.CharScores.Average() : block.BoxScore;
                    var confidence = Math.Clamp(Math.Min(block.BoxScore, charConfidence) * 100d, 0, 100);
                    tokens.Add(new TextToken(block.Text.Trim(), left, top, right, bottom, confidence));
                }
                result.BoxImg?.Dispose();
                return new TextPage { Width = width, Height = height, Tokens = tokens };
            }
            finally
            {
                Console.SetOut(savedOutput);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OcrLite> GetEngineAsync()
    {
        if (_engine is not null) return _engine;
        var folder = AppPaths.RapidOcrModels;
        var required = new[]
        {
            "ch_PP-OCRv5_mobile_det.onnx",
            "ch_ppocr_mobile_v2.0_cls_infer.onnx",
            "ch_PP-OCRv5_rec_mobile_infer.onnx",
            "ppocrv5_dict.txt"
        };
        var missing = required.Where(x => !File.Exists(Path.Combine(folder, x))).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException($"双引擎复核模型缺失：{string.Join("、", missing)}");

        var engine = new OcrLite
        {
            DetPath = Path.Combine(folder, required[0]),
            ClsPath = Path.Combine(folder, required[1]),
            RecPath = Path.Combine(folder, required[2]),
            KeyDicPath = Path.Combine(folder, required[3]),
            ThreadNum = Math.Clamp(Environment.ProcessorCount / 2, 1, 6)
        };
        await engine.InitModels();
        _engine = engine;
        return engine;
    }
}
