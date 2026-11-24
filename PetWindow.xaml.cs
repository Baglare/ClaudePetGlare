using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
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
    private const uint RaiseWithoutActivation = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private static readonly IntPtr TopMost = new(-1);
    private const int SnapDistance = 24;
    private const int SnapGap = 6;
    private static readonly TimeSpan LongTurn = TimeSpan.FromSeconds(10);

    private readonly PetState state = new();
    private readonly Preferences prefs = Preferences.Load();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer watcher = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource closing = new();
    private readonly TrayIcon tray = new();
    private readonly EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showWait;
    private Task? listener;
    private IntPtr handle;
    private Point? dragOffset;
    private Point pressPoint;
    private bool dragging;
    private Point? savedAnchor;
    private bool fullScreenActive;
    private bool fullScreenHidden;
    private DiagnosticsWindow? diagnostics;
    private SettingsWindow? settings;

    // Connection diagnostics
    private bool listening;
    private string? listenError;
    private HookNotice? lastNotice;
    private DateTime? lastNoticeAt;
    private int noticeCount;
    private TaskCompletionSource<bool>? ping;

    // A short message from Petglare itself, shown while no session needs a bubble.
    private string? systemMessage;
    private bool systemWarning;
    private TimeSpan systemUntil;

    internal PetWindow(EventWaitHandle? showSignal = null)
    {
        this.showSignal = showSignal;
        InitializeComponent();

        var canvas = Pet.MascotCanvas;
        canvas.MouseLeftButtonDown += StartDrag;
        canvas.MouseLeftButtonUp += EndDrag;
        canvas.MouseMove += Drag;
        canvas.LostMouseCapture += FinishDrag;
        canvas.MouseRightButtonUp += MascotMenu;
        Pet.ShrinkRequested += () => ResizeMascot(-10);
        Pet.EnlargeRequested += () => ResizeMascot(10);

        timer.Tick += (_, _) =>
        {
            bool expired = systemMessage is not null && clock.Elapsed >= systemUntil;
            if (expired) systemMessage = null;
            if (state.Advance(clock.Elapsed) || expired) RenderStatus();
        };
        watcher.Tick += (_, _) =>
        {
            CheckFullScreen();
            KeepOnTop();
            // Looking at Claude counts as having seen the finished chats.
            if (state.ShowBadge && ClaudeIsForeground() && state.ClearFinished()) RenderStatus();
        };
        Loaded += (_, _) =>
        {
            ApplyAppearance();
            RestorePosition();
            timer.Start();
            watcher.Start();
            listener = ListenAsync();
            SetUpTray();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            if (showSignal is not null)
                showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                    (_, _) => Dispatcher.InvokeAsync(ShowFromSecondLaunch), null, Timeout.Infinite, false);
            UpdateVisibility();
            RenderStatus();
            RelocateAfterMove();
            CheckHooksAtStartup();
            PetLibrary.Changed += LibraryChanged;
            WorkshopCatalog.SetEnabled(prefs.WorkshopOnline);
        };
        // An unsaved Workshop may refuse to close; then the mascot stays too, so nothing is left half shut.
        Closing += (_, e) => { if (!CloseWorkshop()) e.Cancel = true; };
        Closed += async (_, _) =>
        {
            timer.Stop();
            watcher.Stop();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            showWait?.Unregister(null);
            diagnostics?.Close();
            settings?.Close();
            workshop?.Close();
            PetLibrary.Changed -= LibraryChanged;
            tray.Dispose();
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

    // ───────────────────────────── Events from Claude ─────────────────────────────

    private async Task ListenAsync()
    {
        listening = true;
        try
        {
            await new HookPipe().ListenAsync(notice =>
            {
                _ = Dispatcher.InvokeAsync(() => Receive(notice));
            }, closing.Token);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            listening = false;
            listenError = ex.Message;
            Say("Yerel bağlantı açılamadı", warning: true, seconds: 15);
        }
        listening = false;
    }

    private void Receive(HookNotice notice)
    {
        if (notice.Event == HookEvent.Ping)
        {
            ping?.TrySetResult(true);
            return;
        }
        lastNotice = notice;
        lastNoticeAt = DateTime.Now;
        noticeCount++;
        if (state.Apply(notice, clock.Elapsed) is { } alert) Alert(alert);
        RenderStatus();
    }

    private Task<bool> WaitForPing(TimeSpan timeout)
    {
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ping = waiting;
        _ = Task.Delay(timeout).ContinueWith(_ => waiting.TrySetResult(false), TaskScheduler.Default);
        return waiting.Task;
    }

    private DiagnosticsSnapshot Snapshot() => new(listening, listenError, lastNotice, lastNoticeAt,
        noticeCount, state.Sessions.Count, state.ActiveCount);

    /// <summary>
    /// The folder was moved: Petglare's own hooks and start entry still point at the old,
    /// now missing Petglare.exe. Repair runs only with the user's saved opt-in.
    /// </summary>
    private void RelocateAfterMove()
    {
        bool hooks = false, start = false;
        try { hooks = HookSettings.Relocate(allowed: prefs.AutoRelocate == true); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Text.Json.JsonException) { }
        try { start = AutoStart.Relocate(allowed: prefs.AutoRelocate == true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
        if (hooks || start) Say("Petglare yeni klasörüne taşındı; Claude bağlantısı güncellendi", warning: false, seconds: 8);
    }

    private void CheckHooksAtStartup()
    {
        var inspection = HookSettings.Inspect();
        if (inspection.Installed) return;
        var text = inspection.Partial
            ? "Hook’lar güncel değil. Sağ tık → Bağlantı teşhisi"
            : "Claude’a bağlı değil. Sağ tık → Bağlantı teşhisi";
        Say(text, warning: true, seconds: 12);
        tray.Notify("Petglare", inspection.Partial
            ? "Yeni durumlar için hook’ların güncellenmesi gerekiyor. Menüden Bağlantı teşhisi’ni aç."
            : "Petglare Claude’a bağlı değil. Menüden Bağlantı teşhisi’ni aç.", Forms.ToolTipIcon.Warning);
    }

    // ───────────────────────────── Notifications ─────────────────────────────

    private void Alert(PetAlert alert)
    {
        bool wanted = alert.Kind switch
        {
            AlertKind.Attention => prefs.NotifyAttention,
            AlertKind.Complete => prefs.NotifyComplete && alert.TurnLength >= LongTurn,
            AlertKind.Error => prefs.NotifyError,
            _ => false
        };
        if (!wanted || (prefs.QuietWhenClaudeFocused && ClaudeIsForeground())) return;
        Notify(prefs, tray, alert.Kind, alert.Project is null ? "Claude" : $"Claude · {alert.Project}",
            alert.Kind == AlertKind.Complete && alert.TurnLength >= TimeSpan.FromMinutes(1)
                ? $"{alert.Message} ({(int)alert.TurnLength.TotalMinutes} dk sürdü)"
                : alert.Message);
    }

    /// <summary>Shows a Windows notification and/or plays a short sound, as the user chose.</summary>
    private static void Notify(Preferences prefs, TrayIcon tray, AlertKind kind, string title, string text)
    {
        if (prefs.ShowToasts)
            tray.Notify(title, text, kind switch
            {
                AlertKind.Error => Forms.ToolTipIcon.Error,
                AlertKind.Attention => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info
            });
        if (prefs.PlaySound)
            (kind switch
            {
                AlertKind.Attention => SystemSounds.Exclamation,
                AlertKind.Error => SystemSounds.Hand,
                _ => SystemSounds.Asterisk
            }).Play();
    }

    private static bool ClaudeIsForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    // ───────────────────────────── Rendering ─────────────────────────────

    private void Say(string message, bool warning, int seconds)
    {
        systemMessage = message;
        systemWarning = warning;
        systemUntil = clock.Elapsed + TimeSpan.FromSeconds(seconds);
        RenderStatus();
    }

    internal static BubbleModel ToBubble(SessionStatus session) => new(session.Project, session.Message,
        session.Phase switch
        {
            PetPhase.Complete => BubbleTone.Done,
            PetPhase.Error => BubbleTone.Error,
            PetPhase.Attention => BubbleTone.Attention,
            _ when session.Warning => BubbleTone.Warning,
            _ => BubbleTone.Normal
        });

    private void RenderStatus()
    {
        var speaking = state.Speaking;
        var bubbles = new List<BubbleModel>(speaking.Select(ToBubble));
        if (bubbles.Count == 0 && systemMessage is not null)
            bubbles.Add(new BubbleModel(null, systemMessage, systemWarning ? BubbleTone.Warning : BubbleTone.Normal));
        Pet.Render(prefs, bubbles, state.ShowBadge, state.ActiveCount);

        var summary = SessionSummary();
        Pet.MascotCanvas.ToolTip = summary;
        tray.SetText(summary);
        tray.Update(speaking.FirstOrDefault()?.Phase switch
        {
            PetPhase.Attention => Color.FromRgb(0xFF, 0xC4, 0x3D),
            PetPhase.Error => Color.FromRgb(0xFF, 0x6B, 0x7A),
            PetPhase.Working => Color.FromRgb(0x7C, 0xB7, 0xFF),
            PetPhase.Complete => Color.FromRgb(0x6F, 0xE3, 0xB3),
            _ => null
        });
    }

    private string SessionSummary()
    {
        if (state.Sessions.Count == 0) return "Petglare · oturum yok";
        var text = new StringBuilder("Petglare");
        foreach (var session in OrderedSessions().Take(5))
            text.Append('\n').Append(session.Title).Append(" · ").Append(session.Summary);
        return text.ToString();
    }

    private SessionStatus[] OrderedSessions() => state.Sessions
        .OrderByDescending(s => s.Priority).ThenByDescending(s => s.LastEvent).ToArray();

    private void ApplyAppearance()
    {
        Pet.ApplyLook(prefs);
        Topmost = prefs.AlwaysOnTop;
        Pet.Interactive = !LeftClickLocked;
        RenderStatus();
    }

    // ───────────────────────────── Settings ─────────────────────────────

    private void OpenSettings()
    {
        if (settings is { IsLoaded: true })
        {
            settings.Activate();
            return;
        }
        settings = new SettingsWindow(prefs, ApplySettings,
            sample => Notify(sample, tray, AlertKind.Complete, "Claude · Petglare", "Bildirimler böyle görünür."),
            MoveToCorner);
        settings.Closed += (_, _) => settings = null;
        settings.Show();
        settings.Activate();
    }

    private WorkshopWindow? workshop;

    /// <summary>Closes the standalone Workshop first; false when the user kept unsaved work open.</summary>
    internal bool CloseWorkshop()
    {
        if (workshop is null) return true;
        workshop.Close();
        return workshop is null || !workshop.IsVisible;
    }

    /// <summary>An edited or deleted pet shows up (or falls back to the cat) right away.</summary>
    private void LibraryChanged() => Dispatcher.BeginInvoke(ApplyAppearance);

    /// <summary>Makes a new pet and puts it on the desktop once saved.</summary>
    private void OpenWorkshop()
    {
        if (workshop is { IsLoaded: true })
        {
            workshop.Activate();
            return;
        }
        workshop = new WorkshopWindow(null, pet =>
        {
            prefs.Mascot = pet.Key;
            prefs.Save();
            ApplyAppearance();
        }, prefs);
        workshop.Closed += (_, _) => workshop = null;
        workshop.Show();
        workshop.Activate();
    }

    /// <summary>Adopts settings chosen in the settings window, keeping the mascot where it stands.</summary>
    private void ApplySettings(Preferences chosen)
    {
        Relayout(() => prefs.CopySettingsFrom(chosen));
        ApplyAppearance();
        WorkshopCatalog.SetEnabled(prefs.WorkshopOnline);
        SavePreferences();
        prefs.Save();
        CheckFullScreen();
        KeepOnTop();
    }

    // ───────────────────────────── Menu and tray ─────────────────────────────

    private void SetUpTray()
    {
        tray.LeftClick += () => SetHidden(!prefs.Hidden);
        tray.RightClick += () => OpenMenu(fromTray: true);
        tray.NotificationClick += OpenClaude;
        tray.Show();
    }

    private void MascotMenu(object sender, MouseButtonEventArgs e)
    {
        OpenMenu(fromTray: false);
        e.Handled = true;
    }

    private void OpenMenu(bool fromTray)
    {
        var menu = BuildMenu();
        menu.Placement = PlacementMode.MousePoint;
        if (!fromTray) menu.PlacementTarget = Pet.MascotCanvas;
        // A menu opened from the notification area closes on an outside click
        // only when its window is in the foreground.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    /// <summary>A short menu: the chats, then the few things done often. Everything else lives in Settings.</summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var sessions = OrderedSessions();

        menu.Items.Add(new MenuItem
        {
            Header = sessions.Length == 0 ? "Henüz Claude oturumu yok"
                : $"{sessions.Length} oturum · {state.ActiveCount} çalışıyor",
            IsEnabled = false
        });
        foreach (var session in sessions.Take(6))
        {
            var age = DiagnosticsWindow.Ago(clock.Elapsed - session.LastEvent);
            menu.Items.Add(new MenuItem
            {
                Header = $"{session.Title} — {session.Summary} · {age}",
                Icon = Dot(session.Phase),
                IsEnabled = false
            });
        }
        int idle = state.IdleCount;
        var clear = Item(idle > 0 ? $"Boştaki oturumları temizle ({idle})" : "Boştaki oturumları temizle", () =>
        {
            state.ClearIdle();
            RenderStatus();
        });
        clear.IsEnabled = idle > 0;
        clear.ToolTip = "Biten ve hareketsiz oturumları listeden çıkarır. Çalışan veya senden bir şey bekleyen oturumlar kalır.";
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ayarlar…", OpenSettings));
        menu.Items.Add(Item("Pet Workshop…", OpenWorkshop));
        menu.Items.Add(Item("Claude Desktop’ı aç", OpenClaude));
        menu.Items.Add(Item(prefs.Hidden ? "Maskotu göster" : "Maskotu gizle", () => SetHidden(!prefs.Hidden)));
        menu.Items.Add(Item("Bağlantı teşhisi…", OpenDiagnostics));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Petglare’ı kapat", Close));
        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static System.Windows.Shapes.Ellipse Dot(PetPhase phase) => new()
    {
        Width = 8, Height = 8,
        Fill = Appearance.Brush(phase switch
        {
            PetPhase.Attention => Color.FromRgb(0xF0, 0xAD, 0x1F),
            PetPhase.Error => Color.FromRgb(0xE0, 0x4F, 0x5F),
            PetPhase.Working => Color.FromRgb(0x4F, 0x93, 0xF0),
            PetPhase.Complete or PetPhase.Badge => Color.FromRgb(0x3C, 0xB3, 0x7F),
            _ => Color.FromRgb(0xA0, 0xA0, 0xA8)
        })
    };

    private void OpenDiagnostics()
    {
        if (diagnostics is { IsLoaded: true })
        {
            diagnostics.Activate();
            return;
        }
        diagnostics = new DiagnosticsWindow(Snapshot, WaitForPing);
        diagnostics.Closed += (_, _) => diagnostics = null;
        diagnostics.Show();
        diagnostics.Activate();
    }

    // ───────────────────────────── Visibility ─────────────────────────────

    private void SetHidden(bool hidden)
    {
        prefs.Hidden = hidden;
        prefs.Save();
        UpdateVisibility();
        if (hidden)
            tray.Notify("Petglare", "Maskot gizlendi. Bildirim alanındaki simgeye tıklayarak geri getirebilirsin.",
                Forms.ToolTipIcon.Info);
    }

    private void ShowFromSecondLaunch()
    {
        fullScreenHidden = false;
        if (prefs.Hidden) SetHidden(false);
        else UpdateVisibility();
        Say("Buradayım", warning: false, seconds: 3);
    }

    private void UpdateVisibility()
    {
        bool visible = !prefs.Hidden && !fullScreenHidden;
        if (visible && !IsVisible)
        {
            Show();
            if (savedAnchor is { } anchor && GetWindowRect(handle, out var bounds))
                savedAnchor = MoveOntoScreen(anchor, bounds);
            KeepOnTop();
        }
        else if (!visible && IsVisible) Hide();
    }

    /// <summary>Left clicks are ignored when the user chose so, always or over a full-screen app.</summary>
    private bool LeftClickLocked => prefs.LeftClick switch
    {
        Preferences.LeftClickOff => true,
        Preferences.LeftClickFullScreenOff => fullScreenActive,
        _ => false
    };

    private void CheckFullScreen()
    {
        bool active = ForegroundIsFullScreen();
        if (active != fullScreenActive)
        {
            fullScreenActive = active;
            Pet.Interactive = !LeftClickLocked;
        }
        bool hide = prefs.HideInFullScreen && active;
        if (hide == fullScreenHidden) return;
        fullScreenHidden = hide;
        UpdateVisibility();
    }

    /// <summary>
    /// Borderless full-screen apps raise themselves above other topmost windows; put the pet back on top.
    /// Exclusive full-screen games draw past every window, which no app can change.
    /// </summary>
    private void KeepOnTop()
    {
        if (prefs.AlwaysOnTop && IsVisible && handle != IntPtr.Zero && dragOffset is null)
            SetWindowPos(handle, TopMost, 0, 0, 0, 0, RaiseWithoutActivation);
    }

    private bool ForegroundIsFullScreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == handle || !GetWindowRect(window, out var rect)) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        var screen = Screen.FromHandle(window);
        var mine = savedAnchor is { } anchor ? Screen.FromPoint(anchor) : Screen.PrimaryScreen;
        if (mine is not null && !mine.DeviceName.Equals(screen.DeviceName, StringComparison.Ordinal)) return false;
        var area = screen.Bounds;
        return rect.Left <= area.Left && rect.Top <= area.Top && rect.Right >= area.Right && rect.Bottom >= area.Bottom;
    }

    // ───────────────────────────── Position ─────────────────────────────

    private void RestorePosition()
    {
        ApplySize();
        if (!GetWindowRect(handle, out var bounds)) return;

        var work = Screen.PrimaryScreen!.WorkingArea;
        var anchor = prefs.X is { } x && prefs.Y is { } y
            ? new Point(x, y)
            : new Point(work.Right - bounds.Width - 18, work.Bottom - bounds.Height - 18);
        savedAnchor = MoveOntoScreen(anchor, bounds);
    }

    private void MoveToCorner()
    {
        if (!GetWindowRect(handle, out var bounds)) return;
        var screen = savedAnchor is { } anchor ? Screen.FromPoint(anchor) : Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        var corner = new Point(work.Right - bounds.Width - 18, work.Bottom - bounds.Height - 18);
        savedAnchor = MoveOntoScreen(corner, bounds);
        SavePreferences();
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        // Return to the saved spot when a monitor comes back; otherwise stay on a visible screen.
        if (!GetWindowRect(handle, out var bounds)) return;
        var anchor = prefs.X is { } x && prefs.Y is { } y ? new Point(x, y) : savedAnchor;
        if (anchor is { } point) savedAnchor = MoveOntoScreen(point, bounds);
    });

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (LeftClickLocked) return;
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
        if (dragOffset is null) return;
        Drag(sender, e);
        bool openClaude = !dragging;
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

        var anchor = CurrentAnchor(bounds);
        if (prefs.SnapToEdges) anchor = Snap(anchor, bounds);
        savedAnchor = MoveOntoScreen(anchor, bounds);
        SavePreferences();
    }

    /// <summary>
    /// Moves the mascot against an edge it was dropped close to: the monitor's real edge (flush)
    /// or the taskbar's edge, whichever is nearer.
    /// </summary>
    private Point Snap(Point anchor, Bounds bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var mascot = Pet.Mascot;
        int mascotWidth = (int)Math.Round(mascot.Width * Pet.MascotScale.ScaleX * dpi.DpiScaleX);
        int rightMargin = (int)Math.Round(mascot.Margin.Right * dpi.DpiScaleX);
        int bottomMargin = (int)Math.Round(mascot.Margin.Bottom * dpi.DpiScaleY);
        int gap = (int)Math.Round(SnapGap * dpi.DpiScaleX);
        int reach = (int)Math.Round(SnapDistance * dpi.DpiScaleX);

        // The anchor is the window origin; the mascot sits at its bottom right.
        int mascotRight = anchor.X + bounds.Width - rightMargin;
        int mascotLeft = mascotRight - mascotWidth;
        int mascotBottom = anchor.Y + bounds.Height - bottomMargin;
        var screen = Screen.FromPoint(new Point(mascotLeft + mascotWidth / 2, mascotBottom - mascotWidth / 2));
        var (dx, dy, top) = SnapOffsets(mascotLeft, mascotRight, mascotBottom, anchor.Y, screen.Bounds, screen.WorkingArea, reach, gap);
        anchor.X += dx;
        anchor.Y = top ?? anchor.Y + dy;
        return anchor;
    }

    /// <summary>
    /// How far to move the mascot to rest on a nearby edge. Screen edges are flush; taskbar edges
    /// (where the work area stops short of the screen) keep a small gap. Null top means no top snap.
    /// </summary>
    internal static (int Dx, int Dy, int? Top) SnapOffsets(int mascotLeft, int mascotRight, int mascotBottom, int windowTop,
        Rectangle screen, Rectangle work, int reach, int gap)
    {
        static int? Nearest(int value, int reach, params int[] targets)
        {
            int? best = null;
            foreach (int target in targets)
                if (Math.Abs(target - value) <= reach && (best is null || Math.Abs(target - value) < Math.Abs(best.Value - value)))
                    best = target;
            return best;
        }
        int dx = 0, dy = 0;
        int? top = null;
        var leftTargets = work.Left > screen.Left ? new[] { screen.Left, work.Left + gap } : new[] { screen.Left };
        var rightTargets = work.Right < screen.Right ? new[] { screen.Right, work.Right - gap } : new[] { screen.Right };
        var bottomTargets = work.Bottom < screen.Bottom ? new[] { screen.Bottom, work.Bottom - gap } : new[] { screen.Bottom };
        var topTargets = work.Top > screen.Top ? new[] { screen.Top, work.Top } : new[] { screen.Top };
        if (Nearest(mascotLeft, reach, leftTargets) is { } left) dx = left - mascotLeft;
        else if (Nearest(mascotRight, reach, rightTargets) is { } right) dx = right - mascotRight;
        if (Nearest(mascotBottom, reach, bottomTargets) is { } bottom) dy = bottom - mascotBottom;
        else if (Nearest(windowTop, reach, topTargets) is { } t) top = t;
        return (dx, dy, top);
    }

    private Point CurrentAnchor(Bounds bounds) => new(
        bounds.Left + (int)Math.Round(Pet.MascotOffset.X * VisualTreeHelper.GetDpi(this).DpiScaleX),
        bounds.Top);

    private void ResizeMascot(int change)
    {
        if (LeftClickLocked) return;
        int size = Math.Clamp(prefs.MascotSize + change, Preferences.MinMascotSize, Preferences.MaxMascotSize);
        if (size == prefs.MascotSize) return;
        Relayout(() => prefs.Size = size);
        Pet.ApplyLook(prefs);
        SavePreferences();
    }

    /// <summary>
    /// Applies a change that resizes the window (mascot size, bubble size or count) while the
    /// mascot keeps its place on screen: its right and bottom edges stay where they were.
    /// </summary>
    private void Relayout(Action change)
    {
        int before = prefs.MascotSize;
        if (!GetWindowRect(handle, out var old))
        {
            change();
            ApplySize();
            return;
        }
        var anchor = CurrentAnchor(old);
        bool shifted = Pet.MascotOffset.X < 0;
        change();
        if (shifted)
            anchor.X += (int)Math.Round(Pet.Mascot.Width * (prefs.MascotSize - before) / 100.0
                * VisualTreeHelper.GetDpi(this).DpiScaleX);
        ApplySize();
        if (!GetWindowRect(handle, out var updated)) return;
        anchor.X += old.Width - updated.Width;
        anchor.Y += old.Height - updated.Height;
        savedAnchor = MoveOntoScreen(anchor, updated);
    }

    private void ApplySize()
    {
        Pet.MascotScale.ScaleX = Pet.MascotScale.ScaleY = prefs.MascotSize / 100.0;
        var size = PetView.WindowSize(prefs);
        Width = size.Width;
        Height = size.Height;
        UpdateLayout();
    }

    private void SavePreferences()
    {
        if (savedAnchor is not { } position) return;
        prefs.X = position.X;
        prefs.Y = position.Y;
        prefs.Monitor = Screen.FromPoint(position).DeviceName;
        prefs.Save();
    }

    /// <summary>Keeps the mascot on the monitor it is over. The whole monitor counts, taskbar included.</summary>
    private Point MoveOntoScreen(Point anchor, Bounds bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var mascot = Pet.Mascot;
        double mascotWidth = mascot.Width * Pet.MascotScale.ScaleX;
        double mascotHeight = mascot.Height * Pet.MascotScale.ScaleY;
        int leftInset = Math.Max(0, bounds.Width -
            (int)Math.Round((mascotWidth + mascot.Margin.Right) * dpi.DpiScaleX));
        var mascotCenter = new Point(
            anchor.X + leftInset + (int)Math.Round(mascotWidth * dpi.DpiScaleX / 2),
            anchor.Y + bounds.Height - (int)Math.Round((mascotHeight / 2 + mascot.Margin.Bottom) * dpi.DpiScaleY));
        var area = Screen.FromPoint(mascotCenter).Bounds;
        var placement = ConstrainPlacement(anchor, area, bounds.Width, bounds.Height, leftInset);
        Pet.MascotOffset.X = (placement.Anchor.X - placement.Window.X) / dpi.DpiScaleX;
        Pet.UpdateBubblePlacement();
        Move(placement.Window);
        return placement.Anchor;
    }

    internal static (Point Anchor, Point Window) ConstrainPlacement(
        Point anchor, Rectangle work, int width, int height, int leftInset)
    {
        // Keep the original window origin in saved positions, including when the
        // mascot moves inside the window to use the space reserved for the bubbles.
        int left = work.Left - leftInset;
        var placed = new Point(
            Math.Clamp(anchor.X, left, Math.Max(left, work.Right - width)),
            Math.Clamp(anchor.Y, work.Top, Math.Max(work.Top, work.Bottom - height)));
        return (placed, new Point(Math.Max(placed.X, work.Left), placed.Y));
    }

    private void OpenClaude()
    {
        if (state.ClearFinished()) RenderStatus();
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
}
