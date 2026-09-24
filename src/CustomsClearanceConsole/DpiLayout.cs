namespace CustomsClearanceConsole;

/// <summary>
/// Deterministic high-DPI layout.
///
/// The forms used to set AutoScaleMode/AutoScaleDimensions in their constructors outside
/// SuspendLayout. WinForms performs the auto-scale immediately at that point, while the form is
/// still empty, so only the window itself grew by the display scale; every child added
/// afterwards kept its 96-DPI bounds while point-sized text grew with the display. At 150% this
/// clipped labels and pager buttons, and the main window (1440x900 logical = 2160x1350) no
/// longer fitted a 1920x1080 screen, pushing the caption buttons off-screen.
///
/// Forms are now built in 96-DPI logical pixels with auto-scaling off and scaled exactly once,
/// when the window exists and its real DPI is known, and again on every WM_DPICHANGED.
/// </summary>
internal static class DpiLayout
{
    public const int LogicalDpi = 96;

    public static int Scale(int logical, int dpi) =>
        (int)Math.Round(logical * dpi / (double)LogicalDpi, MidpointRounding.AwayFromZero);

    public static Size Scale(Size logical, int dpi) => new(Scale(logical.Width, dpi), Scale(logical.Height, dpi));

    /// <summary>Scales every child (bounds, margins, paddings, recursively) from one DPI to another.</summary>
    public static void ScaleChildren(Control root, int fromDpi, int toDpi)
    {
        if (fromDpi == toDpi || fromDpi <= 0 || toDpi <= 0) return;
        var ratio = toDpi / (float)fromDpi;
        var factor = new SizeF(ratio, ratio);
        foreach (Control child in root.Controls) child.Scale(factor);
    }

    /// <summary>
    /// Converts a width measured by TextRenderer without a device context (which uses the system
    /// DPI) back into logical pixels, so constructor-time measurements are not scaled twice.
    /// </summary>
    public static int MeasuredToLogical(int measured)
    {
        using var screen = Graphics.FromHwnd(IntPtr.Zero);
        var systemDpi = screen.DpiX > 0 ? screen.DpiX : LogicalDpi;
        return (int)Math.Ceiling(measured * LogicalDpi / systemDpi);
    }
}

/// <summary>
/// Base for the borderless dialogs: built in logical pixels, scaled to the real window DPI when
/// the handle is created (keeping the position the presenter centred it on) and on DPI changes.
/// </summary>
internal class DpiDialog : Form
{
    private int _layoutDpi = DpiLayout.LogicalDpi;

    /// <summary>The DPI the dialog's child bounds are currently expressed in.</summary>
    internal int LayoutDpi => _layoutDpi;

    public DpiDialog()
    {
        AutoScaleMode = AutoScaleMode.None;
    }

    protected int Px(int logical) => DpiLayout.Scale(logical, _layoutDpi);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RescaleTo(DeviceDpi, resizeWindow: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Windows already applied the suggested (linearly scaled) window rectangle.
        base.OnDpiChanged(e);
        RescaleTo(e.DeviceDpiNew, resizeWindow: false);
    }

    private void RescaleTo(int dpi, bool resizeWindow)
    {
        if (dpi == _layoutDpi || dpi <= 0) return;
        var center = new Point(Left + Width / 2, Top + Height / 2);
        var client = ClientSize;
        SuspendLayout();
        try
        {
            DpiLayout.ScaleChildren(this, _layoutDpi, dpi);
            if (resizeWindow)
                ClientSize = new Size(
                    (int)Math.Round(client.Width * dpi / (double)_layoutDpi),
                    (int)Math.Round(client.Height * dpi / (double)_layoutDpi));
            _layoutDpi = dpi;
        }
        finally { ResumeLayout(true); }
        if (resizeWindow && StartPosition == FormStartPosition.Manual)
            Location = new Point(center.X - Width / 2, center.Y - Height / 2);
        OnLayoutDpiChanged();
    }

    /// <summary>Hook for DPI-dependent values that Control.Scale does not cover.</summary>
    protected virtual void OnLayoutDpiChanged()
    {
    }
}
