using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Petglare;

public partial class PetWindow : Window
{
    private const int ExtendedStyle = -20;
    private const int NoActivate = 0x08000000;
    private const int MouseActivate = 0x0021;
    private const int DoNotActivate = 3;
    private const uint MoveOnlyWithoutActivation = 0x0001 | 0x0004 | 0x0010;
    private const double BaseHeight = 143;
    private const int DefaultSize = 100;

    private static readonly Brush LightText = new SolidColorBrush(Color.FromRgb(245, 241, 255));
    private static readonly Brush GreenText = new SolidColorBrush(Color.FromRgb(169, 244, 216));
    private static readonly Brush ErrorText = new SolidColorBrush(Color.FromRgb(255, 189, 197));
    private static readonly string PositionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petglare", "position.json");

    private readonly PetState state = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource closing = new();
    private System.Threading.Tasks.Task? listener;
    private IntPtr handle;
    private Point? dragOffset;
    private Point pressPoint;
    private bool dragging;
    private int mascotSize = DefaultSize;
    private Point? savedAnchor;

    public PetWindow()
    {
        InitializeComponent();
        timer.Tick += (_, _) =>
        {
            if (state.Advance(clock.Elapsed)) RenderStatus();
        };
        Loaded += (_, _) =>
        {
            RestorePosition();
            timer.Start();
            listener = ListenAsync();
        };
        Closed += async (_, _) =>
        {
            timer.Stop();
            closing.Cancel();
            if (listener is not null) await listener;
            closing.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        handle = source.Handle;
        SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | NoActivate);
        source.AddHook(WindowMessage);
    }

    private static IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != MouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(DoNotActivate);
    }

    private async System.Threading.Tasks.Task ListenAsync()
    {
        try
        {
            await new HookPipe().ListenAsync(notice =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    state.Apply(notice, clock.Elapsed);
                    RenderStatus();
                });
            }, closing.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Yerel bağlantı açılamadı";
            StatusText.Foreground = ErrorText;
            Bubble.Visibility = Visibility.Visible;
        }
    }

    private void RenderStatus()
    {
        Bubble.Visibility = state.Phase is PetPhase.Idle or PetPhase.Badge
            ? Visibility.Collapsed : Visibility.Visible;
        DoneSpark.Visibility = state.Phase == PetPhase.Badge && SparkMenu.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = state.Message;
        StatusText.FontSize = state.Phase == PetPhase.Complete ? 22 : 13;
        StatusText.Foreground = state.Phase switch
        {
            PetPhase.Complete => GreenText,
            PetPhase.Error => ErrorText,
            _ => LightText
        };
    }

    private void RestorePosition()
    {
        SavedPosition? saved = null;
        try { saved = JsonSerializer.Deserialize<SavedPosition>(File.ReadAllText(PositionFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        mascotSize = Math.Clamp(saved?.Size ?? DefaultSize, 80, 150);
        SparkMenu.IsChecked = saved?.ShowSpark ?? true;
        ApplySize();
        if (!GetWindowRect(handle, out var bounds)) return;

        var work = Screen.PrimaryScreen!.WorkingArea;
        var anchor = saved is null
            ? new Point(work.Right - bounds.Width - 18, work.Bottom - bounds.Height - 18)
            : new Point(saved.X, saved.Y);
        savedAnchor = MoveIntoWorkArea(anchor, bounds);
    }

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(handle, out var bounds)) return;
        if (!((UIElement)sender).CaptureMouse()) return;
        dragOffset = new Point(cursor.X - bounds.Left, cursor.Y - bounds.Top);
        pressPoint = cursor;
        dragging = false;
        e.Handled = true;
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (dragOffset is not { } offset || !GetCursorPos(out var cursor)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!dragging && Math.Abs(cursor.X - pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
            && Math.Abs(cursor.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
            return;
        dragging = true;
        Move(new Point(cursor.X - offset.X, cursor.Y - offset.Y));
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        Drag(sender, e);
        bool openClaude = dragOffset is not null && !dragging;
        ((UIElement)sender).ReleaseMouseCapture();
        if (openClaude) OpenClaude();
        e.Handled = true;
    }

    private void FinishDrag(object sender, MouseEventArgs e)
    {
        if (dragOffset is null) return;
        dragOffset = null;
        if (!dragging) return;
        dragging = false;
        if (!GetWindowRect(handle, out var bounds)) return;

        savedAnchor = MoveIntoWorkArea(CurrentAnchor(bounds), bounds);
        SavePreferences();
    }

    private Point CurrentAnchor(Bounds bounds) => new(
        bounds.Left + (int)Math.Round(MascotOffset.X * VisualTreeHelper.GetDpi(this).DpiScaleX),
        bounds.Top);

    private void ToggleSpark(object sender, RoutedEventArgs e)
    {
        RenderStatus();
        SavePreferences();
    }

    private void Shrink(object sender, RoutedEventArgs e) => ResizeMascot(-10);
    private void Enlarge(object sender, RoutedEventArgs e) => ResizeMascot(10);

    private void ResizeMascot(int change)
    {
        int size = Math.Clamp(mascotSize + change, 80, 150);
        if (size == mascotSize || !GetWindowRect(handle, out var before)) return;
        var anchor = CurrentAnchor(before);
        if (MascotOffset.X < 0)
            anchor.X += (int)Math.Round(Mascot.Width * (size - mascotSize) / 100.0
                * VisualTreeHelper.GetDpi(this).DpiScaleX);
        mascotSize = size;
        ApplySize();
        if (!GetWindowRect(handle, out var after)) return;
        anchor.Y += before.Height - after.Height;
        savedAnchor = MoveIntoWorkArea(anchor, after);
        SavePreferences();
    }

    private void ApplySize()
    {
        MascotScale.ScaleX = MascotScale.ScaleY = mascotSize / 100.0;
        Height = BaseHeight + Math.Ceiling(Mascot.Height * Math.Max(0, MascotScale.ScaleY - 1));
        UpdateLayout();
    }

    private void SavePreferences()
    {
        if (savedAnchor is not { } position) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PositionFile)!);
            var temporary = PositionFile + ".tmp";
            var preferences = new SavedPosition(position.X, position.Y, mascotSize, SparkMenu.IsChecked);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, PositionFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Point MoveIntoWorkArea(Point anchor, Bounds bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double mascotWidth = Mascot.Width * MascotScale.ScaleX;
        double mascotHeight = Mascot.Height * MascotScale.ScaleY;
        int leftInset = Math.Max(0, bounds.Width -
            (int)Math.Round((mascotWidth + Mascot.Margin.Right) * dpi.DpiScaleX));
        var mascotCenter = new Point(
            anchor.X + leftInset + (int)Math.Round(mascotWidth * dpi.DpiScaleX / 2),
            anchor.Y + bounds.Height - (int)Math.Round((mascotHeight / 2 + Mascot.Margin.Bottom) * dpi.DpiScaleY));
        var work = Screen.FromPoint(mascotCenter).WorkingArea;
        var placement = ConstrainPlacement(anchor, work, bounds.Width, bounds.Height, leftInset);
        MascotOffset.X = (placement.Anchor.X - placement.Window.X) / dpi.DpiScaleX;
        UpdateBubblePlacement();
        Move(placement.Window);
        return placement.Anchor;
    }

    private static (Point Anchor, Point Window) ConstrainPlacement(
        Point anchor, Rectangle work, int width, int height, int leftInset)
    {
        // Keep the original window origin in saved positions, including when the
        // mascot moves inside the window to use the space reserved for the bubble.
        int left = work.Left - leftInset;
        var placed = new Point(
            Math.Clamp(anchor.X, left, Math.Max(left, work.Right - width)),
            Math.Clamp(anchor.Y, work.Top, Math.Max(work.Top, work.Bottom - height)));
        return (placed, new Point(Math.Max(placed.X, work.Left), placed.Y));
    }

    private void BubbleSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBubblePlacement();

    private void UpdateBubblePlacement()
    {
        double availableShift = Math.Max(0, ActualWidth - Bubble.ActualWidth - Bubble.Margin.Left - Bubble.Margin.Right);
        BubbleOffset.X = Math.Max(MascotOffset.X, -availableShift);
        double tail = MascotOffset.X - BubbleOffset.X + Mascot.Width * (1 - MascotScale.ScaleX) / 2;
        TailOffset.X = Math.Clamp(tail, -Math.Max(0, Bubble.ActualWidth - 40), 28);
    }

    private static void OpenClaude()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude"));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Claude Desktop açılamadı.", "Petglare", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Move(Point point) =>
        SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, MoveOnlyWithoutActivation);

    private void ExitPet(object sender, RoutedEventArgs e) => Close();

    private sealed record SavedPosition(int X, int Y, int? Size = null, bool? ShowSpark = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
