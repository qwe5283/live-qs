using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveQs.Windows.App.Controls;

public static class AppIconFactory
{
    public static ImageSource CreateImageSource()
    {
        const int size = 64;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(00, 122, 255)), null, new Point(32, 32), 28, 28);
            var clockPen = new Pen(Brushes.White, 5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            context.DrawEllipse(null, clockPen, new Point(32, 32), 16, 16);
            context.DrawLine(clockPen, new Point(32, 22), new Point(32, 34));
            context.DrawLine(clockPen, new Point(32, 34), new Point(41, 39));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(226, 126, 86)), null, new Point(52, 13), 5, 5);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
