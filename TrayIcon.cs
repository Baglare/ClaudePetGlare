using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Petglare;

/// <summary>The notification-area icon: shows the overall status and carries Windows notifications.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon = new();
    private IntPtr iconHandle;
    private string? renderedKey;

    internal event Action? LeftClick;
    internal event Action? RightClick;
    internal event Action? NotificationClick;

    internal TrayIcon()
    {
        icon.Text = "Petglare";
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) RightClick?.Invoke();
        };
        icon.BalloonTipClicked += (_, _) => NotificationClick?.Invoke();
    }

    internal void Show() => icon.Visible = true;

    internal void SetText(string text) =>
        icon.Text = text.Length <= 127 ? text : text[..126] + "…";

    /// <summary>Draws the mascot with a small status dot. Re-renders only when something changed.</summary>
    internal void Update(FrameworkElement mascot, Color? dot, string paletteKey)
    {
        var key = $"{paletteKey}|{dot}";
        if (key == renderedKey) return;
        renderedKey = key;

        const int size = 32;
        mascot.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (mascot.ActualWidth > 0 && mascot.ActualHeight > 0)
            {
                // Render at twice the size first so the small icon stays smooth.
                var source = new RenderTargetBitmap((int)Math.Ceiling(mascot.ActualWidth * 2),
                    (int)Math.Ceiling(mascot.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                source.Render(mascot);
                double scale = size / Math.Max(mascot.ActualWidth, mascot.ActualHeight);
                double width = mascot.ActualWidth * scale, height = mascot.ActualHeight * scale;
                context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
            }
            if (dot is { } color)
            {
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x21)), null,
                    new Point(size - 7, size - 7), 7, 7);
                context.DrawEllipse(new SolidColorBrush(color), null, new Point(size - 7, size - 7), 5, 5);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var image = new System.Drawing.Bitmap(stream);

        var old = iconHandle;
        iconHandle = image.GetHicon();
        icon.Icon = System.Drawing.Icon.FromHandle(iconHandle);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    internal void Notify(string title, string text, Forms.ToolTipIcon kind) =>
        icon.ShowBalloonTip(5000, title, text, kind);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
        iconHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
