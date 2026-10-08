using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveQs.Windows.Converters;

public sealed class ApplicationIconConverter : IValueConverter
{
    private const uint ShgfiIcon = 0x00000100;
    private static readonly ImageSource FallbackIcon = CreateFallbackIcon();
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return FallbackIcon;
        if (_icons.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return FallbackIcon;

        var info = new ShellFileInfo();
        try
        {
            // SHGFI_ICON returns a caller-owned HICON; copy it before releasing the handle.
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon) == nint.Zero
                || info.Icon == nint.Zero)
                return FallbackIcon;

            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            _icons[path] = image;
            return image;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or ExternalException)
        {
            return FallbackIcon;
        }
        finally
        {
            if (info.Icon != nint.Zero) DestroyIcon(info.Icon);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static ImageSource CreateFallbackIcon()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var outline = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            var surface = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            context.DrawRoundedRectangle(surface, new Pen(outline, 1.2), new Rect(1, 2, 18, 16), 2, 2);
            context.DrawLine(new Pen(outline, 1.2), new Point(1, 7), new Point(19, 7));
            context.DrawEllipse(outline, null, new Point(4, 4.5), 0.7, 0.7);
            context.DrawEllipse(outline, null, new Point(7, 4.5), 0.7, 0.7);
            context.DrawRoundedRectangle(outline, null, new Rect(4, 10, 5, 5), 0.6, 0.6);
            context.DrawLine(new Pen(outline, 1.2), new Point(11, 11), new Point(16, 11));
            context.DrawLine(new Pen(outline, 1.2), new Point(11, 14), new Point(16, 14));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
