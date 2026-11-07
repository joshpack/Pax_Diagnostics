using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace PaxDiagnostics;

/// <summary>The app's mark (a monitor with a pulse line across it), drawn from vector geometry so it stays crisp at every size.</summary>
public static class AppIcon
{
    /// <summary>The monitor: screen outline and stand, stroked, in a 100x100 box.</summary>
    public static readonly Geometry Monitor = Geometry.Parse("M14,16 L86,16 Q92,16 92,22 L92,66 Q92,72 86,72 L14,72 Q8,72 8,66 L8,22 Q8,16 14,16 Z M50,72 L50,86 M32,88 L68,88");

    /// <summary>The heartbeat line across the screen, stroked.</summary>
    public static readonly Geometry Pulse = Geometry.Parse("M18,46 L34,46 L41,32 L50,60 L58,26 L65,46 L82,46");

    private static Control Build(int size)
    {
        var dark = new SolidColorBrush(Color.Parse("#04140C"));
        var tile = new Grid { Width = size, Height = size };
        tile.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#00C8FF"), 0), new GradientStop(Color.Parse("#19F58C"), 1) },
            },
        });

        // Thin strokes vanish at 16 px; small icons get bolder lines.
        var weight = size <= 24 ? 12 : size <= 48 ? 9.5 : 8;
        var art = new Grid { Width = 100, Height = 100 };
        art.Children.Add(new ShapePath { Data = Monitor, Stroke = dark, StrokeThickness = weight, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        art.Children.Add(new ShapePath { Data = Pulse, Stroke = dark, StrokeThickness = weight, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        tile.Children.Add(new Viewbox { Margin = new Thickness(size * 0.13), Child = art });
        return tile;
    }

    private static RenderTargetBitmap Render(int size)
    {
        var mark = Build(size);
        mark.Measure(new Size(size, size));
        mark.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(mark);
        return bitmap;
    }

    public static WindowIcon Create(int size = 128) => new(Render(size));

    /// <summary>
    /// Build step: writes a multi-size .ico (PNG entries) for the exe's embedded icon, or a single 256 px PNG when the
    /// path ends in .png.
    /// </summary>
    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = Render(256);
            png.Save(path, PngBitmapEncoderOptions.Default);
            return;
        }

        int[] sizes = [256, 64, 48, 32, 24, 16];
        var pngs = sizes.Select(s =>
        {
            using var bmp = Render(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }).ToList();

        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((short)0);
        bw.Write((short)1);
        bw.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            bw.Write(b);
            bw.Write(b);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var p in pngs) bw.Write(p);
    }
}
