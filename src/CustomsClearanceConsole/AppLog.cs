using System.Text;

namespace CustomsClearanceConsole;

internal static class AppLog
{
    private static readonly object Gate = new();
    public static string Folder => AppPaths.DataFolder;
    public static string FilePath => Path.Combine(Folder, "app.log");

    public static void Write(Exception ex) => Write(ex.ToString());

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void ShowUnexpected(Exception ex)
    {
        Write(ex);
        MessageBox.Show($"程序遇到意外错误，已写入日志：\n{FilePath}\n\n{ex.Message}", "关单核验台", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
