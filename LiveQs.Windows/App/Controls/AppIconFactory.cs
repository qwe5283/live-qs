using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LiveQs.Windows.App.Controls;

public static partial class AppIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var background = new SolidBrush(System.Drawing.Color.FromArgb(37, 133, 111));
        graphics.FillEllipse(background, 4, 4, 56, 56);
        using var facePen = new System.Drawing.Pen(System.Drawing.Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(facePen, 16, 16, 32, 32);
        graphics.DrawLine(facePen, 32, 22, 32, 34);
        graphics.DrawLine(facePen, 32, 34, 41, 39);
        using var dot = new SolidBrush(System.Drawing.Color.FromArgb(226, 126, 86));
        graphics.FillEllipse(dot, 47, 8, 10, 10);

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { _ = DestroyIcon(handle); }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint iconHandle);
}
