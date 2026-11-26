using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AFKtor.Tray;

/// <summary>Draws the tray icons at startup so the app ships no image assets.</summary>
internal static class TrayIcons
{
    public static WindowIcon Create(Color fill)
    {
        const int size = 32;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 2),
                new Point(size / 2.0, size / 2.0), size / 2.0 - 2, size / 2.0 - 2);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
