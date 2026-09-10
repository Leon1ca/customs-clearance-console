namespace CustomsClearanceConsole;

internal sealed class VerificationForm : Form
{
    private readonly DeclarationRecord _record;
    private readonly string _targetFolder;
    private readonly CancellationTokenSource _lifetime = new();
    private BrowserValidation? _browser;
    private readonly Label _status;
    private readonly Button _capture;
    private bool _captureInProgress;

    public string? SavedScreenshot { get; private set; }

    public VerificationForm(DeclarationRecord record, string targetFolder, bool autoStart = true)
    {
        _record = record;
        _targetFolder = targetFolder;
        Text = $"核验 · {record.DeclarationNo}";
        ClientSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var frame = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 12,
            BorderColor = ColorTranslator.FromHtml("#AEBED1"),
            BackColor = Color.White
        };
        var title = new Label
        {
            Text = "网页核验与截图留存",
            Font = Theme.UiFont(24F, FontStyle.Bold),
            ForeColor = Theme.Navy,
            Location = new Point(32, 25),
            Size = new Size(500, 38),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var closeGlyph = new CircleCloseButton { Location = new Point(564, 20), AccessibleName = "关闭核验窗口" };
        closeGlyph.Click += (_, _) => Close();
        var no = new Label
        {
            Text = $"报关单号  {record.DeclarationNo}",
            Location = new Point(34, 72),
            Size = new Size(560, 26),
            ForeColor = Theme.FieldText,
            Font = Theme.UiFont(16F)
        };
        var statusSurface = new RoundedPanel
        {
            Location = new Point(32, 265),
            Size = new Size(576, 90),
            Radius = 8,
            BorderColor = ColorTranslator.FromHtml("#D8E3F1"),
            BackColor = ColorTranslator.FromHtml("#F4F8FD")
        };
        _status = new Label
        {
            Text = "正在打开中国国际贸易单一窗口……",
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 0, 18, 0),
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(15F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        statusSurface.Controls.Add(_status);

        var hint = new Label
        {
            Text = "网页结果和单号确认后自动留存。90 秒未确认时，可检查网页后手动保存。",
            Location = new Point(34, 364),
            Size = new Size(574, 40),
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(13F),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var close = Theme.SecondaryButton("关闭");
        close.Location = new Point(374, 414);
        close.Size = new Size(108, 44);
        close.Click += (_, _) => Close();
        _capture = Theme.PrimaryButton("手动留存");
        _capture.Location = new Point(494, 414);
        _capture.Size = new Size(114, 44);
        _capture.Enabled = false;
        _capture.Click += async (_, _) =>
        {
            if (ConfirmationDialog.Confirm(this, "确认手动留存？", $"请确认网页显示的报关单号为 {_record.DeclarationNo}，且查询结果已完整显示。", "截图只表示已留存，不会将识别异常标记为通过。", "确认留存")) await CaptureAsync(manualConfirmed: true);
        };

        var steps = new TableLayoutPanel { Location = new Point(34, 112), Size = new Size(570, 138), ColumnCount = 2, RowCount = 3 };
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36)); steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var stepTitles = new[] { "打开核验网页并填写单号", "在网页输入验证码，然后点击查询", "确认单号与结果后，自动保存长截图" };
        for (var i = 0; i < 3; i++)
        {
            steps.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            steps.Controls.Add(new Label { Text = (i + 1).ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Blue, Font = Theme.UiFont(16, FontStyle.Bold) }, 0, i);
            steps.Controls.Add(new Label { Text = stepTitles[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.UiFont(14) }, 1, i);
        }
        frame.Controls.AddRange([title, closeGlyph, no, steps, statusSurface, hint, close, _capture]);
        Controls.Add(frame);
        CancelButton = close;
        if (autoStart) Shown += async (_, _) => await StartAndMonitorAsync();
        FormClosed += (_, _) =>
        {
            _lifetime.Cancel();
            var browser = _browser;
            _browser = null;
            if (browser is not null) _ = DisposeBrowserSafelyAsync(browser);
        };
    }

    private async Task StartAndMonitorAsync()
    {
        try
        {
            _browser = new BrowserValidation();
            _status.Text = await _browser.StartAsync(_record.DeclarationNo, _lifetime.Token);
            var updates = new Progress<string>(message => { if (!IsDisposed) _status.Text = message; });
            var ready = await _browser.WaitForStableResultAsync(TimeSpan.FromSeconds(90), updates, _lifetime.Token);
            if (ready) await CaptureAsync();
            else _capture.Enabled = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            _status.Text = "自动检测连接不可用。请保留已打开的网页，必要时使用浏览器网页捕获功能。";
            _status.ForeColor = Theme.Warning;
            _capture.Enabled = _browser is not null;
        }
    }

    private async Task CaptureAsync(bool manualConfirmed = false)
    {
        if (_browser is null || _captureInProgress) return;
        try
        {
            _captureInProgress = true;
            _capture.Enabled = false;
            _status.ForeColor = Theme.Muted;
            _status.Text = "正在展开网页滚动区域并生成长截图……";
            SavedScreenshot = await _browser.CaptureLongScreenshotAsync(_record.DeclarationNo, _targetFolder, _lifetime.Token, manualConfirmed);
            _status.Text = "长截图已保存。";
            _status.ForeColor = Theme.Success;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            _capture.Enabled = true;
            _status.Text = $"截图未保存：{ex.Message}";
            _status.ForeColor = Theme.Warning;
        }
        finally { _captureInProgress = false; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 12F);
        Region = new Region(path);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int csDropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= csDropShadow;
            return parameters;
        }
    }

    private static async Task DisposeBrowserSafelyAsync(BrowserValidation browser)
    {
        try { await browser.DisposeAsync(); }
        catch (Exception ex) { AppLog.Write($"关闭浏览器控制连接时已忽略异常：{ex.Message}"); }
    }
}
