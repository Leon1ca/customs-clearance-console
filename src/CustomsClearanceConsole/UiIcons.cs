using System.Drawing.Drawing2D;

namespace CustomsClearanceConsole;

internal enum UiIcon { Directory, DeclarationClean, ScreenshotClean, Start, ListClean, Folder, Export, Clean }
internal enum MetricIcon { File, Duplicate, Gross, Deduplicated }

internal static class UiIcons
{
    public static Bitmap LoadButton(UiIcon icon, bool primary, float dpi)
    {
        var size = dpi >= 168F ? 48 : dpi >= 120F ? 32 : 24;
        var stem = icon switch
        {
            UiIcon.Directory => "directory-settings",
            UiIcon.DeclarationClean => "declaration-clean",
            UiIcon.ScreenshotClean => "screenshot-clean",
            UiIcon.Start => primary ? "start-recognition-white" : "start-recognition-blue",
            UiIcon.ListClean => "list-clean",
            UiIcon.Export => "export",
            UiIcon.Clean => "clean",
            _ => ""
        };
        if (stem.Length > 0)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "button-icons", $"{stem}-{size}.png");
            if (File.Exists(path))
            {
                using var source = new Bitmap(path);
                return new Bitmap(source);
            }
        }
        var color = primary ? Color.White : icon is UiIcon.DeclarationClean or UiIcon.ScreenshotClean or UiIcon.ListClean
            ? ColorTranslator.FromHtml("#A9231F")
            : ColorTranslator.FromHtml("#254E68");
        return Create(icon, color, size);
    }

    public static Bitmap Create(UiIcon icon, Color color, int size = 24)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size / 24F, size / 24F);
        using var pen = new Pen(color, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case UiIcon.Directory:
                graphics.DrawLines(pen, new PointF[] { new(3.5F, 7.25F), new(3.5F, 6F), new(4F, 4.7F), new(5F, 4.5F), new(9.55F, 4.5F), new(11.75F, 6.75F), new(19F, 6.75F), new(20.5F, 8.25F), new(20.5F, 18F), new(19F, 19.5F), new(5F, 19.5F), new(3.5F, 18F), new(3.5F, 7.25F) });
                graphics.DrawLine(pen, 7, 11, 17, 11); graphics.DrawLine(pen, 9.25F, 9.65F, 9.25F, 12.35F);
                graphics.DrawLine(pen, 7, 15.5F, 17, 15.5F); graphics.DrawLine(pen, 14.75F, 14.15F, 14.75F, 16.85F);
                break;
            case UiIcon.DeclarationClean:
                graphics.DrawLines(pen, new PointF[] { new(7.25F, 3.5F), new(13.35F, 3.5F), new(17.75F, 7.9F), new(17.75F, 19.5F), new(16.75F, 20.5F), new(7.25F, 20.5F), new(6.25F, 19.5F), new(6.25F, 4.5F), new(7.25F, 3.5F) });
                graphics.DrawLines(pen, new PointF[] { new(13.35F, 3.5F), new(13.35F, 7.9F), new(17.75F, 7.9F) });
                graphics.DrawLine(pen, 9.25F, 12.25F, 14.75F, 17.75F); graphics.DrawLine(pen, 14.75F, 12.25F, 9.25F, 17.75F);
                break;
            case UiIcon.ScreenshotClean:
                graphics.DrawRectangle(pen, 3.5F, 4.25F, 17F, 15.5F);
                graphics.DrawLines(pen, new PointF[] { new(5.25F, 18.1F), new(9.55F, 13.55F), new(12.55F, 16.5F), new(14.9F, 14.05F), new(18.75F, 18.1F) });
                graphics.DrawLine(pen, 15.7F, 7.25F, 18.8F, 10.35F); graphics.DrawLine(pen, 18.8F, 7.25F, 15.7F, 10.35F);
                break;
            case UiIcon.Start:
                graphics.DrawLines(pen, new PointF[] { new(8F, 4F), new(5.75F, 4F), new(4F, 5.75F), new(4F, 8F) });
                graphics.DrawLines(pen, new PointF[] { new(16F, 4F), new(18.25F, 4F), new(20F, 5.75F), new(20F, 8F) });
                graphics.DrawLines(pen, new PointF[] { new(20F, 16F), new(20F, 18.25F), new(18.25F, 20F), new(16F, 20F) });
                graphics.DrawLines(pen, new PointF[] { new(8F, 20F), new(5.75F, 20F), new(4F, 18.25F), new(4F, 16F) });
                graphics.DrawRectangle(pen, 9F, 7F, 8F, 10F); graphics.DrawLine(pen, 7F, 13.25F, 17F, 13.25F);
                break;
            case UiIcon.ListClean:
                foreach (var y in new[] { 6.25F, 11.25F, 16.25F }) { graphics.DrawLine(pen, 4.5F, y, 6F, y); graphics.DrawLine(pen, 9F, y, y == 16.25F ? 13F : 19.5F, y); }
                graphics.DrawLine(pen, 15.25F, 14.5F, 19.5F, 18.75F); graphics.DrawLine(pen, 19.5F, 14.5F, 15.25F, 18.75F);
                break;
            case UiIcon.Folder:
                graphics.DrawLines(pen, new PointF[] { new(3F, 7F), new(10F, 7F), new(12F, 9F), new(21F, 9F), new(21F, 19F), new(3F, 19F), new(3F, 7F) });
                break;
        }
        return bitmap;
    }

    public static Bitmap CreateMetric(MetricIcon icon)
    {
        var bitmap = new Bitmap(42, 42);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (back, fore) = icon switch
        {
            MetricIcon.File => (ColorTranslator.FromHtml("#E8F1FF"), ColorTranslator.FromHtml("#254E68")),
            MetricIcon.Duplicate => (ColorTranslator.FromHtml("#FFEDEA"), ColorTranslator.FromHtml("#D7372F")),
            MetricIcon.Gross => (ColorTranslator.FromHtml("#F2EAFF"), ColorTranslator.FromHtml("#6E45D6")),
            _ => (ColorTranslator.FromHtml("#E6F7ED"), ColorTranslator.FromHtml("#14965F"))
        };
        using (var background = new SolidBrush(back))
        using (var path = Theme.RoundedPath(new RectangleF(0, 0, 42, 42), 8)) graphics.FillPath(background, path);
        using var pen = new Pen(fore, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (icon)
        {
            case MetricIcon.File:
                graphics.DrawLines(pen, new PointF[] { new(15, 12), new(23, 12), new(27, 16), new(27, 30), new(15, 30), new(15, 12) });
                graphics.DrawLines(pen, new PointF[] { new(23, 12), new(23, 17), new(27, 17) });
                graphics.DrawLine(pen, 18, 21, 24, 21); graphics.DrawLine(pen, 18, 25, 24, 25);
                break;
            case MetricIcon.Duplicate:
                graphics.DrawRectangle(pen, 13, 13, 11, 11); graphics.DrawRectangle(pen, 18, 18, 11, 11);
                break;
            case MetricIcon.Gross:
                graphics.DrawEllipse(pen, 12, 12, 18, 18);
                graphics.DrawLines(pen, new PointF[] { new(17.5F, 16.5F), new(21, 20), new(24.5F, 16.5F) });
                graphics.DrawLine(pen, 21, 20, 21, 27); graphics.DrawLine(pen, 17.5F, 22, 24.5F, 22);
                break;
            case MetricIcon.Deduplicated:
                graphics.DrawEllipse(pen, 12, 12, 18, 18); graphics.DrawLines(pen, new PointF[] { new(16.5F, 21), new(19.5F, 24), new(25.5F, 17) });
                break;
        }
        return bitmap;
    }
}
