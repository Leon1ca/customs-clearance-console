using System.Text;

namespace CustomsClearanceConsole;

internal static partial class AppLog
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

}
