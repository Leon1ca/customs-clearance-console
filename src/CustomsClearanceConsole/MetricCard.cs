namespace CustomsClearanceConsole;

internal sealed class MetricCard : RoundedPanel
{
    private readonly Label _value;

    public MetricCard(string title, string unit, MetricIcon icon)
    {
        BackColor = Theme.Surface;
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var image = UiIcons.CreateMetric(icon);
        var picture = new PictureBox { Image = image, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, Margin = Padding.Empty, AccessibleName = title + "图标" };
        picture.Disposed += (_, _) => image.Dispose();
        heading.Controls.Add(picture, 0, 0);
        heading.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = Theme.UiFont(13, FontStyle.Bold), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        _value = new Label { Text = "0", AutoSize = true, Font = Theme.UiFont(32, FontStyle.Bold), ForeColor = Theme.Text, Margin = Padding.Empty };
        var values = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
        values.Controls.Add(_value);
        values.Controls.Add(new Label { Text = unit, AutoSize = true, Font = Theme.UiFont(14), ForeColor = Theme.Muted, Margin = new Padding(8, 14, 0, 0) });
        Padding = new Padding(16, 10, 12, 10);
        Controls.Add(values); Controls.Add(heading);
    }

    public void Set(string value) { _value.Text = value; _value.AccessibleDescription = value; }
}
