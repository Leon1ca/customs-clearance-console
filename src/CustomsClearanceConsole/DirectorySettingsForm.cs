namespace CustomsClearanceConsole;

internal sealed class DirectorySettingsForm : Form
{
    private readonly TextBox _declarationFolder;
    private readonly TextBox _screenshotFolder;
    private readonly Label _helper;

    public string DeclarationFolder => _declarationFolder.Text.Trim();
    public string ScreenshotFolder => _screenshotFolder.Text.Trim();

    public DirectorySettingsForm(string declarationFolder, string screenshotFolder)
    {
        Text = "目录设置";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ClientSize = new Size(780, 475);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var frame = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 12,
            BorderColor = ColorTranslator.FromHtml("#AEBED1"),
            BackColor = Color.White,
            Margin = new Padding(0)
        };

        var header = new Panel { Location = new Point(1, 1), Size = new Size(778, 109), BackColor = ColorTranslator.FromHtml("#F6F8FB") };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ColorTranslator.FromHtml("#D5DEE9"));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        header.Controls.Add(new Label
        {
            Text = "目录设置",
            Location = new Point(31, 25),
            Size = new Size(300, 36),
            Font = Theme.UiFont(26F, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft
        });
        header.Controls.Add(new Label
        {
            Text = "分别设置关单读取目录与核验截图保存目录",
            Location = new Point(32, 65),
            Size = new Size(560, 24),
            Font = Theme.UiFont(16F),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        });
        var close = new CircleCloseButton { Location = new Point(702, 29) };
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        header.Controls.Add(close);
        header.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDialogDrag(); };

        var declarationLabel = FieldLabel("关单读取目录", 32, 121);
        var declarationField = new PathDisplay(declarationFolder, "尚未选择关单读取目录", out _declarationFolder)
        { Location = new Point(32, 150), Size = new Size(572, 56) };
        var chooseDeclaration = DirectoryButton();
        chooseDeclaration.Location = new Point(620, 150);
        chooseDeclaration.Click += (_, _) => ChooseFolder(_declarationFolder, "选择关单读取目录");

        var screenshotLabel = FieldLabel("截图保存目录", 32, 233);
        var screenshotField = new PathDisplay(screenshotFolder, "尚未选择截图保存目录", out _screenshotFolder)
        { Location = new Point(32, 262), Size = new Size(572, 56) };
        var chooseScreenshot = DirectoryButton();
        chooseScreenshot.Location = new Point(620, 262);
        chooseScreenshot.Click += (_, _) => ChooseFolder(_screenshotFolder, "选择截图保存目录");

        _helper = new Label
        {
            Text = "路径仅保存在当前设备，可随时重新设置。",
            Location = new Point(32, 344),
            Size = new Size(716, 22),
            Font = Theme.UiFont(14F),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        var footer = new Panel { Location = new Point(1, 383), Size = new Size(778, 91), BackColor = Color.White };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(ColorTranslator.FromHtml("#D5DEE9"));
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var cancel = Theme.SecondaryButton("取消");
        cancel.Location = new Point(511, 20);
        cancel.Size = new Size(112, 48);
        cancel.DialogResult = DialogResult.Cancel;
        var save = Theme.PrimaryButton("保存设置");
        save.Location = new Point(635, 20);
        save.Size = new Size(112, 48);
        save.Click += (_, _) => SaveAndClose();
        footer.Controls.AddRange([cancel, save]);

        frame.Controls.AddRange([
            header, declarationLabel, declarationField, chooseDeclaration,
            screenshotLabel, screenshotField, chooseScreenshot, _helper, footer
        ]);
        Controls.Add(frame);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Label FieldLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(300, 22),
        Font = Theme.UiFont(16F, FontStyle.Bold),
        ForeColor = Theme.FieldText,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Button DirectoryButton()
    {
        var button = Theme.TonalButton("选择目录");
        button.Size = new Size(128, 56);
        if (button is RoundedButton rounded) rounded.Radius = 10;
        return button;
    }

    private void ChooseFolder(TextBox target, string title)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dialog.SelectedPath;
        ShowHelper("路径仅保存在当前设备，可随时重新设置。", false);
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(DeclarationFolder) || !Directory.Exists(DeclarationFolder))
        {
            ShowHelper("请选择有效的关单读取目录。", true);
            _declarationFolder.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ScreenshotFolder) || !Directory.Exists(ScreenshotFolder))
        {
            ShowHelper("请选择有效的截图保存目录。", true);
            _screenshotFolder.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowHelper(string text, bool error)
    {
        _helper.Text = text;
        _helper.ForeColor = error ? Theme.Danger : Theme.Muted;
    }

    private void BeginDialogDrag()
    {
        Capture = false;
        var drag = Message.Create(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        WndProc(ref drag);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), 12);
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

    private sealed class PathDisplay : RoundedPanel
    {
        public PathDisplay(string value, string placeholder, out TextBox textBox)
        {
            Radius = 7;
            BorderColor = ColorTranslator.FromHtml("#C7D2E1");
            BackColor = Color.White;
            Padding = new Padding(50, 14, 16, 10);
            textBox = new TextBox
            {
                Text = value,
                PlaceholderText = placeholder,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Theme.FieldText,
                Font = Theme.UiFont(17F),
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            Controls.Add(textBox);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var icon = UiIcons.Create(UiIcon.Folder, Theme.Placeholder, 24);
            e.Graphics.DrawImage(icon, 16, (Height - 24) / 2, 24, 24);
        }
    }
}
