namespace CustomsClearanceConsole;

internal sealed class MoneySummaryPanel : RoundedPanel
{
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
    private string _fingerprint = "";
    public int CurrencyCount { get; private set; }

    public MoneySummaryPanel()
    {
        BackColor = Theme.Surface; Padding = new Padding(18, 10, 18, 10);
        var title = new Label { Text = "金额汇总 · 本批全部记录", Dock = DockStyle.Top, Height = 28, ForeColor = Theme.Text, Font = Theme.UiFont(13, FontStyle.Bold) };
        var note = new Label { Text = "仅计入已确认金额；不同币种分别统计", Dock = DockStyle.Bottom, Height = 20, ForeColor = Theme.Muted, Font = Theme.UiFont(11) };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scroll.Controls.Add(_table);
        Controls.Add(scroll); Controls.Add(note); Controls.Add(title);
    }

    public void Set(Dictionary<string, decimal> gross, Dictionary<string, decimal> net)
    {
        var currencies = gross.Keys.Union(net.Keys).OrderBy(x => x).ToList(); CurrencyCount = currencies.Count;
        var fingerprint = string.Join("|", currencies.Select(x => $"{x}:{gross.GetValueOrDefault(x)}:{net.GetValueOrDefault(x)}"));
        if (_fingerprint == fingerprint && _table.Controls.Count > 0) return;
        _fingerprint = fingerprint;
        using var measureFont = Theme.MonoFont(14);
        var amountWidth = gross.Values.Concat(net.Values).Select(x => TextRenderer.MeasureText(x.ToString("N2"), measureFont).Width + 24).DefaultIfEmpty(130).Max();
        _table.MinimumSize = new Size((int)(70 * DeviceDpi / 96F) + amountWidth * 2, 0);
        _table.SuspendLayout();
        foreach (Control control in _table.Controls.Cast<Control>().ToArray()) control.Dispose();
        _table.Controls.Clear(); _table.RowStyles.Clear(); _table.RowCount = Math.Max(1, currencies.Count) + 1;
        Add("币种", 0, 0, false); Add("去重前", 1, 0, false); Add("去重后", 2, 0, false);
        if (currencies.Count == 0) { Add("—", 0, 1, true); Add("—", 1, 1, true); Add("—", 2, 1, true); }
        for (var i = 0; i < currencies.Count; i++)
        {
            var currency = currencies[i]; Add(currency, 0, i + 1, true);
            Add(gross.GetValueOrDefault(currency).ToString("N2"), 1, i + 1, true);
            Add(net.GetValueOrDefault(currency).ToString("N2"), 2, i + 1, true);
        }
        _table.ResumeLayout();
    }

    private void Add(string text, int column, int row, bool value)
    {
        var label = new Label { Text = text, Dock = DockStyle.Fill, Height = (int)(27 * DeviceDpi / 96F), Margin = Padding.Empty,
            Font = value ? Theme.MonoFont(14) : Theme.UiFont(11), ForeColor = value ? Theme.Text : Theme.Muted,
            TextAlign = column == 0 ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight,
            AutoEllipsis = false, AccessibleName = text };
        _table.Controls.Add(label, column, row);
    }
}
