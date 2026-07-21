using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveQs.Windows.App.Controls;

public enum TrayIconState
{
    Local,
    Paused,
    CloudConnected,
    CloudUnavailable,
}

public static class AppIconFactory
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/LiveQs.ico", UriKind.Absolute);
    private static readonly Lazy<BitmapSource> BaseIcon = new(LoadBaseIcon);
    private static readonly Lazy<IReadOnlyDictionary<TrayIconState, ImageSource>> TrayIcons = new(CreateTrayIcons);

    public static ImageSource CreateApplicationIcon() => BaseIcon.Value;

    public static ImageSource CreateTrayIcon(TrayIconState state) => TrayIcons.Value[state];

    private static BitmapSource LoadBaseIcon()
    {
        var resource = Application.GetResourceStream(IconUri)
            ?? throw new InvalidOperationException("The embedded LiveQs icon resource could not be loaded.");
        using (resource.Stream)
        {
            var decoder = new IconBitmapDecoder(
                resource.Stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - 64))
                .ThenBy(candidate => Math.Abs(candidate.PixelHeight - 64))
                .First();
            var icon = frame.Clone();
            icon.Freeze();
            return icon;
        }
    }

    private static IReadOnlyDictionary<TrayIconState, ImageSource> CreateTrayIcons() =>
        new Dictionary<TrayIconState, ImageSource>
        {
            [TrayIconState.Local] = BaseIcon.Value,
            [TrayIconState.Paused] = DrawStatusDot(Color.FromRgb(226, 126, 86)),
            [TrayIconState.CloudConnected] = DrawStatusDot(Color.FromRgb(85, 183, 158)),
            [TrayIconState.CloudUnavailable] = DrawStatusDot(Color.FromRgb(126, 138, 144)),
        };

    private static ImageSource DrawStatusDot(Color color)
    {
        const int size = 64;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(BaseIcon.Value, new Rect(0, 0, size, size));
            context.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 1.5), new Point(52, 13), 10, 10);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
