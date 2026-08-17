using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CustomsLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var host = Path.Combine(root, "runtime", "dotnet.exe");
            var appFolder = Path.Combine(root, "app");
            var app = Path.Combine(appFolder, "关单核验台.dll");
            if (!File.Exists(host) || !File.Exists(app))
            {
                MessageBox.Show("程序组件不完整。请完整解压压缩包后，再双击“关单核验台.exe”。", "关单核验台", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                var arguments = new StringBuilder();
                arguments.Append(Quote(app));
                foreach (var arg in args) arguments.Append(' ').Append(Quote(arg));
                var start = new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = arguments.ToString(),
                    WorkingDirectory = appFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var process = Process.Start(start);
                if (args.Length > 0 && process != null)
                {
                    process.WaitForExit();
                    Environment.ExitCode = process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序启动失败：" + ex.Message, "关单核验台", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
