using System;
using System.IO;
using System.Linq;
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

    /// <summary>The app icon (Assets\Petglare.ico), shared by the tray and the app's windows.</summary>
    internal static readonly BitmapDecoder AppIconDecoder = BitmapDecoder.Create(
        new Uri("pack://application:,,,/Assets/Petglare.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    internal static BitmapFrame AppIcon => AppIconDecoder.Frames[0];

    /// <summary>Draws the app icon with a small status dot. Re-renders only when the dot changed.</summary>
    internal void Update(Color? dot)
    {
        var key = dot?.ToString() ?? "-";
        if (key == renderedKey) return;
        renderedKey = key;

        const int size = 32;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // The .ico carries hand-tuned 16-256 px frames; use the exact 32 px one when present.
            var frame = AppIconDecoder.Frames.FirstOrDefault(f => f.PixelWidth == size)
                ?? AppIconDecoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            context.DrawImage(frame, new Rect(0, 0, size, size));
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
