using System;
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
using System.Windows.Documents;
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
    private const double BaseHeight = 143;
    private const int DefaultSize = 100;
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
    private int mascotSize = DefaultSize;
    private Point? savedAnchor;
    private bool fullScreenHidden;
    private DiagnosticsWindow? diagnostics;

    // Connection diagnostics
    private bool listening;
    private string? listenError;
    private HookNotice? lastNotice;
    private DateTime? lastNoticeAt;
    private int noticeCount;
    private TaskCompletionSource<bool>? ping;

    // A short message from Petglare itself, shown while no session needs the bubble.
    private string? systemMessage;
    private bool systemWarning;
    private TimeSpan systemUntil;

    internal PetWindow(EventWaitHandle? showSignal = null)
    {
        this.showSignal = showSignal;
        InitializeComponent();
        timer.Tick += (_, _) =>
        {
            bool expired = systemMessage is not null && clock.Elapsed >= systemUntil;
            if (expired) systemMessage = null;
            if (state.Advance(clock.Elapsed) || expired) RenderStatus();
        };
        watcher.Tick += (_, _) => CheckFullScreen();
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
            CheckHooksAtStartup();
        };
        Closed += async (_, _) =>
        {
            timer.Stop();
            watcher.Stop();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            showWait?.Unregister(null);
            diagnostics?.Close();
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

        if (prefs.ShowToasts)
        {
            var title = alert.Project is null ? "Claude" : $"Claude · {alert.Project}";
            var text = alert.Kind == AlertKind.Complete && alert.TurnLength >= TimeSpan.FromMinutes(1)
                ? $"{alert.Message} ({(int)alert.TurnLength.TotalMinutes} dk sürdü)"
                : alert.Message;
            tray.Notify(title, text, alert.Kind switch
            {
                AlertKind.Error => Forms.ToolTipIcon.Error,
                AlertKind.Attention => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info
            });
        }
        if (prefs.PlaySound)
        {
            (alert.Kind switch
            {
                AlertKind.Attention => SystemSounds.Exclamation,
                AlertKind.Error => SystemSounds.Hand,
                _ => SystemSounds.Asterisk
            }).Play();
        }
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

    private void RenderStatus()
    {
        var style = Appearance.Bubble(prefs.Bubble);
        var focus = state.Focus;
        StatusText.Inlines.Clear();
        StatusText.FontSize = prefs.FontSize;

        var border = style.Border;
        if (focus is not null)
        {
            bool complete = focus.Phase == PetPhase.Complete;
            var color = focus.Phase switch
            {
                PetPhase.Complete => style.Done,
                PetPhase.Error => style.Error,
                PetPhase.Attention => style.Warning,
                _ when focus.Warning => style.Warning,
                _ => style.Text
            };
            if (prefs.ShowProject && focus.Project is not null)
            {
                StatusText.Inlines.Add(new Run(focus.Project)
                    { Foreground = Appearance.Brush(style.Dim), FontWeight = FontWeights.Normal });
                StatusText.Inlines.Add(new Run(complete ? "  " : " · ")
                    { Foreground = Appearance.Brush(style.Dim), FontWeight = FontWeights.Normal });
            }
            StatusText.Inlines.Add(new Run(focus.Message)
            {
                Foreground = Appearance.Brush(color),
                FontSize = complete ? 22 : prefs.FontSize,
                BaselineAlignment = BaselineAlignment.Center
            });
            if (focus.Phase == PetPhase.Attention) border = style.Warning;
            else if (focus.Phase == PetPhase.Error) border = style.Error;
        }
        else if (systemMessage is not null)
        {
            StatusText.Inlines.Add(new Run(systemMessage)
                { Foreground = Appearance.Brush(systemWarning ? style.Warning : style.Text) });
        }

        Bubble.Visibility = focus is not null || systemMessage is not null ? Visibility.Visible : Visibility.Collapsed;
        BubbleBorder.Background = BubbleTail.Fill = Appearance.Brush(style.Background);
        BubbleBorder.BorderBrush = Appearance.Brush(border);
        DoneSpark.Visibility = state.ShowBadge && focus is null && prefs.ShowSpark != false
            ? Visibility.Visible : Visibility.Collapsed;

        int active = state.ActiveCount;
        SessionBadge.Visibility = active >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = active.ToString();
        SessionBadge.ToolTip = $"{active} oturum çalışıyor";

        var summary = SessionSummary();
        MascotCanvas.ToolTip = summary;
        tray.SetText(summary);
        tray.Update(MascotCanvas, focus?.Phase switch
        {
            PetPhase.Attention => Color.FromRgb(0xFF, 0xC4, 0x3D),
            PetPhase.Error => Color.FromRgb(0xFF, 0x6B, 0x7A),
            PetPhase.Working => Color.FromRgb(0x7C, 0xB7, 0xFF),
            PetPhase.Complete => Color.FromRgb(0x6F, 0xE3, 0xB3),
            _ => null
        }, $"{prefs.Mascot}|{prefs.Palette}");
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
        var shape = Appearance.Shape(prefs.Mascot);
        var palette = Appearance.Colors(prefs.Palette);
        Body.Data = Geometry.Parse(shape.Body);
        Body.Fill = Appearance.Brush(palette.Body);
        Body.Stroke = Appearance.Brush(palette.Outline);
        Ears.Visibility = shape.Ears ? Visibility.Visible : Visibility.Collapsed;
        Ears.Fill = Cheeks.Stroke = Appearance.Brush(palette.Accent);
        LeftEye.Fill = RightEye.Fill = Appearance.Brush(palette.Eyes);
        SessionBadge.BorderBrush = Appearance.Brush(palette.Body);
        Opacity = Math.Clamp(prefs.Opacity, 30, 100) / 100.0;
        RenderStatus();
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
        if (!fromTray) menu.PlacementTarget = MascotCanvas;
        // A menu opened from the notification area closes on an outside click
        // only when its window is in the foreground.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var sessions = OrderedSessions();

        var header = new MenuItem
        {
            Header = sessions.Length == 0 ? "Henüz Claude oturumu yok"
                : $"{sessions.Length} oturum · {state.ActiveCount} çalışıyor",
            IsEnabled = false
        };
        menu.Items.Add(header);
        foreach (var session in sessions.Take(8))
        {
            var age = DiagnosticsWindow.Ago(clock.Elapsed - session.LastEvent);
            menu.Items.Add(new MenuItem
            {
                Header = $"{session.Title} — {session.Summary} · {age}",
                Icon = Dot(session.Phase),
                IsEnabled = false
            });
        }
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Claude Desktop’ı aç", OpenClaude));
        menu.Items.Add(Item(prefs.Hidden ? "Maskotu göster" : "Maskotu gizle", () => SetHidden(!prefs.Hidden)));
        menu.Items.Add(new Separator());

        var notify = new MenuItem { Header = "Bildirimler" };
        notify.Items.Add(Check("Windows bildirimi göster", prefs.ShowToasts, v => prefs.ShowToasts = v));
        notify.Items.Add(Check("Kısa ses çal", prefs.PlaySound, v => prefs.PlaySound = v));
        notify.Items.Add(new Separator());
        notify.Items.Add(Check("Onay veya yanıt beklediğinde", prefs.NotifyAttention, v => prefs.NotifyAttention = v));
        notify.Items.Add(Check("Uzun bir yanıt bittiğinde", prefs.NotifyComplete, v => prefs.NotifyComplete = v));
        notify.Items.Add(Check("Hata olduğunda", prefs.NotifyError, v => prefs.NotifyError = v));
        notify.Items.Add(new Separator());
        notify.Items.Add(Check("Claude öndeyken sessiz kal", prefs.QuietWhenClaudeFocused,
            v => prefs.QuietWhenClaudeFocused = v));
        notify.Items.Add(Item("Örnek bildirim gönder", () =>
        {
            if (prefs.ShowToasts) tray.Notify("Claude · Petglare", "Bildirimler böyle görünür.", Forms.ToolTipIcon.Info);
            if (prefs.PlaySound) SystemSounds.Asterisk.Play();
        }));
        menu.Items.Add(notify);

        var look = new MenuItem { Header = "Görünüm" };
        look.Items.Add(Choice("Maskot", Appearance.Shapes.Select(s => (s.Name, s.Name)), prefs.Mascot,
            v => prefs.Mascot = v));
        look.Items.Add(Choice("Renk", Appearance.Palettes.Select(p => (p.Name, p.Name)), prefs.Palette,
            v => prefs.Palette = v));
        look.Items.Add(Choice("Balon", Appearance.Bubbles.Select(b => (b.Name, b.Name)), prefs.Bubble,
            v => prefs.Bubble = v));
        look.Items.Add(Choice("Saydamlık", Appearance.Opacities.Select(o => (o == 100 ? "Opak" : $"%{o}", o)),
            prefs.Opacity, v => prefs.Opacity = v));
        look.Items.Add(Choice("Yazı boyutu", Appearance.FontSizes.Select(f => (f.Name, f.Size)), prefs.FontSize,
            v => prefs.FontSize = v));
        look.Items.Add(new Separator());
        look.Items.Add(Check("Proje adını göster", prefs.ShowProject, v => prefs.ShowProject = v));
        look.Items.Add(Check("Tamamlanma parıltısını göster", prefs.ShowSpark != false, v => prefs.ShowSpark = v));
        menu.Items.Add(look);

        var place = new MenuItem { Header = "Konum" };
        place.Items.Add(Check("Ekran kenarlarına yasla", prefs.SnapToEdges, v => prefs.SnapToEdges = v));
        place.Items.Add(Check("Tam ekran uygulamalarda gizlen", prefs.HideInFullScreen, v =>
        {
            prefs.HideInFullScreen = v;
            CheckFullScreen();
        }));
        place.Items.Add(new Separator());
        place.Items.Add(Item("Sağ alt köşeye taşı", MoveToCorner));
        if (Screen.AllScreens.Length > 1)
        {
            var screens = new MenuItem { Header = "Ekrana taşı" };
            int index = 1;
            foreach (var screen in Screen.AllScreens)
            {
                var target = screen;
                screens.Items.Add(Item($"Ekran {index++}{(screen.Primary ? " (birincil)" : "")}",
                    () => MoveToCorner(target)));
            }
            place.Items.Add(screens);
        }
        menu.Items.Add(place);

        bool autoStart = SafeAutoStart();
        menu.Items.Add(Check("Windows açılırken başlat", autoStart, v =>
        {
            try { AutoStart.Set(v); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                           or System.IO.IOException)
            {
                Say("Otomatik başlatma ayarlanamadı", warning: true, seconds: 6);
            }
        }, save: false));
        menu.Items.Add(Item("Bağlantı teşhisi…", OpenDiagnostics));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Petglare’ı kapat", Close));
        return menu;
    }

    private static bool SafeAutoStart()
    {
        try { return AutoStart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem Check(string header, bool value, Action<bool> set, bool save = true)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value, StaysOpenOnClick = false };
        item.Click += (_, _) =>
        {
            set(item.IsChecked);
            if (save) prefs.Save();
            ApplyAppearance();
        };
        return item;
    }

    private MenuItem Choice<T>(string header, System.Collections.Generic.IEnumerable<(string Name, T Value)> options,
        T current, Action<T> set)
    {
        var parent = new MenuItem { Header = header };
        foreach (var (name, value) in options)
        {
            var item = new MenuItem
            {
                Header = name, IsCheckable = true,
                IsChecked = System.Collections.Generic.EqualityComparer<T>.Default.Equals(value, current)
            };
            item.Click += (_, _) =>
            {
                set(value);
                prefs.Save();
                ApplyAppearance();
            };
            parent.Items.Add(item);
        }
        return parent;
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
        if (hidden && !SafeAutoStart())
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
                savedAnchor = MoveIntoWorkArea(anchor, bounds);
        }
        else if (!visible && IsVisible) Hide();
    }

    private void CheckFullScreen()
    {
        bool hide = prefs.HideInFullScreen && ForegroundIsFullScreen();
        if (hide == fullScreenHidden) return;
        fullScreenHidden = hide;
        UpdateVisibility();
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
        mascotSize = Math.Clamp(prefs.Size ?? DefaultSize, 80, 150);
        ApplySize();
        if (!GetWindowRect(handle, out var bounds)) return;

        var work = Screen.PrimaryScreen!.WorkingArea;
        var anchor = prefs.X is { } x && prefs.Y is { } y
            ? new Point(x, y)
            : new Point(work.Right - bounds.Width - 18, work.Bottom - bounds.Height - 18);
        savedAnchor = MoveIntoWorkArea(anchor, bounds);
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        // Return to the saved spot when a monitor comes back; otherwise stay on a visible screen.
        if (!GetWindowRect(handle, out var bounds)) return;
        var anchor = prefs.X is { } x && prefs.Y is { } y ? new Point(x, y) : savedAnchor;
        if (anchor is { } point) savedAnchor = MoveIntoWorkArea(point, bounds);
    });

    private void MoveToCorner() => MoveToCorner(null);

    private void MoveToCorner(Screen? target)
    {
        if (!GetWindowRect(handle, out var bounds)) return;
        var screen = target ?? (savedAnchor is { } anchor ? Screen.FromPoint(anchor) : Screen.PrimaryScreen!);
        var work = screen.WorkingArea;
        var corner = new Point(work.Right - bounds.Width - 18, work.Bottom - bounds.Height - 18);
        savedAnchor = MoveIntoWorkArea(corner, bounds);
        SavePreferences();
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

        var anchor = CurrentAnchor(bounds);
        if (prefs.SnapToEdges) anchor = Snap(anchor, bounds);
        savedAnchor = MoveIntoWorkArea(anchor, bounds);
        SavePreferences();
    }

    /// <summary>Moves the mascot flush against a work-area edge it was dropped close to.</summary>
    private Point Snap(Point anchor, Bounds bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int mascotWidth = (int)Math.Round(Mascot.Width * MascotScale.ScaleX * dpi.DpiScaleX);
        int rightMargin = (int)Math.Round(Mascot.Margin.Right * dpi.DpiScaleX);
        int bottomMargin = (int)Math.Round(Mascot.Margin.Bottom * dpi.DpiScaleY);
        int gap = (int)Math.Round(SnapGap * dpi.DpiScaleX);
        int reach = (int)Math.Round(SnapDistance * dpi.DpiScaleX);

        // The anchor is the window origin; the mascot sits at its bottom right.
        int mascotRight = anchor.X + bounds.Width - rightMargin;
        int mascotLeft = mascotRight - mascotWidth;
        int mascotBottom = anchor.Y + bounds.Height - bottomMargin;
        var work = Screen.FromPoint(new Point(mascotLeft + mascotWidth / 2, mascotBottom - mascotWidth / 2)).WorkingArea;

        if (Math.Abs(mascotLeft - work.Left) <= reach) anchor.X += work.Left + gap - mascotLeft;
        else if (Math.Abs(work.Right - mascotRight) <= reach) anchor.X += work.Right - gap - mascotRight;
        if (Math.Abs(work.Bottom - mascotBottom) <= reach) anchor.Y += work.Bottom - gap - mascotBottom;
        else if (Math.Abs(anchor.Y - work.Top) <= reach) anchor.Y = work.Top;
        return anchor;
    }

    private Point CurrentAnchor(Bounds bounds) => new(
        bounds.Left + (int)Math.Round(MascotOffset.X * VisualTreeHelper.GetDpi(this).DpiScaleX),
        bounds.Top);

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
        prefs.X = position.X;
        prefs.Y = position.Y;
        prefs.Size = mascotSize;
        prefs.Monitor = Screen.FromPoint(position).DeviceName;
        prefs.Save();
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
