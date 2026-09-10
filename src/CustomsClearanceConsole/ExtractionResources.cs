using System.Diagnostics;

namespace CustomsClearanceConsole;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; }
    public TemporaryDirectory(string root)
    {
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        catch (Exception ex) { AppLog.Write($"临时文件清理失败：{Path} · {ex.Message}"); }
    }
}

internal static class OwnedProcess
{
    public static async Task WaitForExitAsync(Process process, CancellationToken token)
    {
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            // Only stop the process launched for this OCR pass, including its children.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
