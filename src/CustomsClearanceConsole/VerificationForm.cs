namespace CustomsClearanceConsole;

internal sealed class VerificationForm : Form
{
    private readonly DeclarationRecord _record;
    private readonly string _browserPreference;
    private readonly string _targetFolder;
    private BrowserValidation? _browser;
    private readonly Label _status;
    private readonly Button _capture;

    public string? SavedScreenshot { get; private set; }

    public VerificationForm(DeclarationRecord record, string browserPreference, string targetFolder)
    {
        _record = record;
        _browserPreference = browserPreference;
        _targetFolder = targetFolder;
        Text = $"核验 · {record.DeclarationNo}";
        Size = new Size(620, 310);
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;

        var title = new Label { Text = "在线核验与长截图", Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), ForeColor = Theme.Navy, AutoSize = true, Location = new Point(28, 24) };
        var no = new Label { Text = $"报关单号  {record.DeclarationNo}", AutoSize = true, Location = new Point(30, 63), ForeColor = Theme.Text };
        _status = new Label
        {
            Text = "正在打开中国国际贸易单一窗口……", Location = new Point(30, 98), Size = new Size(550, 62),
            ForeColor = Theme.Muted
        };
        var fallback = Theme.SecondaryButton("备用网站");
        fallback.Location = new Point(30, 184); fallback.Size = new Size(112, 38); fallback.Click += async (_, _) => await OpenFallbackAsync();
        _capture = Theme.PrimaryButton("已完成人工验证，保存长截图");
        _capture.Location = new Point(154, 184); _capture.Size = new Size(275, 38); _capture.Enabled = false;
        _capture.Click += async (_, _) => await CaptureAsync();
        var close = Theme.SecondaryButton("关闭"); close.Location = new Point(441, 184); close.Size = new Size(110, 38); close.Click += (_, _) => Close();
        var hint = new Label { Text = "边界处理：同名单据不会覆盖旧截图，将自动追加时间戳。", AutoSize = true, Location = new Point(30, 239), ForeColor = Theme.Muted, Font = new Font(Font.FontFamily, 8.5F) };
        Controls.AddRange([title, no, _status, fallback, _capture, close, hint]);
        Shown += async (_, _) => await StartAsync();
        FormClosed += (_, _) =>
        {
            var browser = _browser;
            _browser = null;
            if (browser is not null) _ = DisposeBrowserSafelyAsync(browser);
        };
    }

    private async Task StartAsync()
    {
        try
        {
            _browser = new BrowserValidation();
            _status.Text = await _browser.StartAsync(_record.DeclarationNo, _browserPreference, CancellationToken.None);
            _capture.Enabled = true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            _status.Text = "网站已尝试打开，但自动控制连接不可用。请在浏览器中手动输入并查询；可使用 Edge/Chrome 的网页捕获保存长截图。";
            _status.ForeColor = Theme.Warning;
            _capture.Enabled = false;
        }
    }

    private async Task OpenFallbackAsync()
    {
        try
        {
            if (_browser is null) return;
            await _browser.NavigateFallbackAsync(_record.DeclarationNo, CancellationToken.None);
            _status.Text = "已切换到备用核验网站并尝试自动填写。请完成人工验证并等待流程信息完整显示。";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task CaptureAsync()
    {
        try
        {
            _capture.Enabled = false; _status.Text = "正在展开页面中的滚动区域并生成长截图……";
            SavedScreenshot = await _browser!.CaptureLongScreenshotAsync(_record.DeclarationNo, _targetFolder, CancellationToken.None);
            _status.Text = $"截图已保存：{SavedScreenshot}";
            _status.ForeColor = Theme.Success;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex); _capture.Enabled = true;
            MessageBox.Show($"截图未保存：{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task DisposeBrowserSafelyAsync(BrowserValidation browser)
    {
        try { await browser.DisposeAsync(); }
        catch (Exception ex) { AppLog.Write($"关闭浏览器控制连接时已忽略异常：{ex.Message}"); }
    }
}
