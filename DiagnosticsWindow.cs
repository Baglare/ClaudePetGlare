using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Petglare;

/// <summary>What the pet knows about its connection to Claude at one moment.</summary>
internal sealed record DiagnosticsSnapshot(bool Listening, string? ListenError,
    HookNotice? LastNotice, DateTime? LastNoticeAt, int NoticeCount, int Sessions, int ActiveSessions);

/// <summary>Answers "is Petglare really connected to Claude?" and offers a repair.</summary>
internal sealed class DiagnosticsWindow : Window
{
    private static readonly Brush Good = Appearance.Brush(Color.FromRgb(0x2E, 0xA0, 0x6E));
    private static readonly Brush Warn = Appearance.Brush(Color.FromRgb(0xE0, 0x9B, 0x1A));
    private static readonly Brush Bad = Appearance.Brush(Color.FromRgb(0xD6, 0x45, 0x50));
    private static readonly Brush Neutral = Appearance.Brush(Color.FromRgb(0x9A, 0x97, 0xA8));

    private readonly Func<DiagnosticsSnapshot> snapshot;
    private readonly Func<TimeSpan, Task<bool>> waitForPing;
    private readonly StackPanel rows = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock testResult = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button repair = new() { Content = "Hook’ları kur / onar", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button test = new() { Content = "Bağlantıyı test et", Padding = new Thickness(12, 5, 12, 5) };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private HookSettings.Inspection inspection = HookSettings.Inspect();
    private DateTime inspectedAt = DateTime.Now;

    internal DiagnosticsWindow(Func<DiagnosticsSnapshot> snapshot, Func<TimeSpan, Task<bool>> waitForPing)
    {
        this.snapshot = snapshot;
        this.waitForPing = waitForPing;
        Title = "Petglare · Bağlantı teşhisi";
        Icon = TrayIcon.AppIcon;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var open = new Button { Content = "Ayar dosyasını göster", Padding = new Thickness(12, 5, 12, 5) };
        var close = new Button { Content = "Kapat", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        repair.Click += (_, _) => Repair();
        test.Click += async (_, _) => await TestAsync();
        open.Click += (_, _) => ShowSettingsFile();
        close.Click += (_, _) => Close();

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var button in new[] { test, repair, open })
        {
            button.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(button);
        }
        close.Margin = new Thickness(0, 0, 0, 6);
        buttons.Children.Add(close);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 14),
            Children =
            {
                new TextBlock { Text = "Bağlantı teşhisi", FontSize = 18, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "Claude’un Petglare’a olay gönderip göndermediğini burada görebilirsin.",
                    Foreground = Neutral, Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap
                },
                rows, testResult, buttons
            }
        };

        refresh.Tick += (_, _) => Render();
        Loaded += (_, _) => { Render(); refresh.Start(); };
        Closed += (_, _) => refresh.Stop();
    }

    private void Render()
    {
        if (DateTime.Now - inspectedAt > TimeSpan.FromSeconds(5))
        {
            inspection = HookSettings.Inspect();
            inspectedAt = DateTime.Now;
        }
        var now = snapshot();
        rows.Children.Clear();

        var (hookBrush, hookText) = inspection switch
        {
            { Error: { } error } => (Bad, $"Ayar dosyası okunamadı: {error}"),
            { FileExists: false } => (Bad, "Claude ayar dosyası yok. “Hook’ları kur / onar” ile kurabilirsin."),
            { Installed: true } => (Good, "Bütün olaylar bu Petglare.exe’ye bağlı."),
            { OtherExecutables.Length: > 0 } => (Warn,
                $"Hook’lar başka bir konumu gösteriyor: {inspection.OtherExecutables[0]}. Onar düğmesi bu konuma taşır."),
            { Partial: true } => (Warn, $"Eksik olaylar: {string.Join(", ", inspection.Missing)}. "
                + "Yeni özellikler için hook’ları onar."),
            _ => (Bad, "Petglare hook’ları kurulu değil.")
        };
        repair.Content = inspection.Installed ? "Hook’ları yeniden kur" : "Hook’ları kur / onar";
        AddRow(hookBrush, "Claude hook’ları", hookText, inspection.Path);

        AddRow(now.Listening ? Good : Bad, "Yerel bağlantı",
            now.Listening ? "Petglare olayları dinliyor." : $"Dinleme başlatılamadı. {now.ListenError}");

        if (now.LastNotice is { } last && now.LastNoticeAt is { } at)
        {
            var age = DateTime.Now - at;
            var project = last.Project is null ? "" : $" · {last.Project}";
            AddRow(age < TimeSpan.FromMinutes(10) ? Good : Neutral, "Son olay",
                $"{HookNotice.NameOf(last.Event)}{project} · {Ago(age)} ({now.NoticeCount} olay alındı)");
        }
        else
        {
            AddRow(Neutral, "Son olay", "Petglare açıldığından beri olay gelmedi. "
                + "Claude’da bir istek gönderince burada görünür.");
        }

        AddRow(now.Sessions > 0 ? Good : Neutral, "Oturumlar", now.Sessions == 0
            ? "İzlenen oturum yok."
            : $"{now.Sessions} oturum izleniyor, {now.ActiveSessions} tanesi çalışıyor.");

        var claude = Process.GetProcessesByName("claude");
        bool desktop = claude.Length > 0;
        foreach (var process in claude) process.Dispose();
        AddRow(desktop ? Good : Neutral, "Claude Desktop", desktop ? "Çalışıyor." : "Çalışmıyor görünüyor.");
    }

    private void AddRow(Brush state, string title, string detail, string? note = null)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });
        if (note is not null)
            text.Children.Add(new TextBlock { Text = note, Foreground = Neutral, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10, Fill = state, Margin = new Thickness(0, 5, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(text);
        rows.Children.Add(row);
    }

    private void Repair()
    {
        try
        {
            HookSettings.Update(HookSettings.DefaultPath(), install: true);
            inspection = HookSettings.Inspect();
            inspectedAt = DateTime.Now;
            SetResult(Good, "Hook’lar güncellendi. Açık Claude oturumları yeni ayarı birkaç saniye içinde okur; "
                + "okumazsa oturumu yeniden başlat.");
        }
        catch (Exception ex)
        {
            SetResult(Bad, $"Hook’lar güncellenemedi. {ex.Message}");
        }
        Render();
    }

    /// <summary>Runs the hook command exactly as Claude would and waits for the pet to hear it.</summary>
    private async Task TestAsync()
    {
        test.IsEnabled = false;
        SetResult(Neutral, "Test ediliyor…");
        try
        {
            var watch = Stopwatch.StartNew();
            var heard = waitForPing(TimeSpan.FromSeconds(4));
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "--hook", HookSettings.Marker },
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                hook_event_name = HookNotice.PingEvent,
                session_id = "petglare-diagnostics"
            }));
            process.StandardInput.Close();
            bool ok = await heard;
            SetResult(ok ? Good : Bad, ok
                ? $"Hook komutu Petglare’a ulaştı ({watch.ElapsedMilliseconds} ms)."
                : "Hook komutu çalıştı ama Petglare’a ulaşmadı.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetResult(Bad, $"Test başlatılamadı. {ex.Message}");
        }
        finally { test.IsEnabled = true; }
    }

    private void ShowSettingsFile()
    {
        var path = inspection.Path;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { File.Exists(path) ? $"/select,{path}" : Path.GetDirectoryName(path)! }
            });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void SetResult(Brush brush, string text)
    {
        testResult.Foreground = brush;
        testResult.Text = text;
    }

    internal static string Ago(TimeSpan age) => age.TotalSeconds switch
    {
        < 5 => "az önce",
        < 60 => $"{(int)age.TotalSeconds} sn önce",
        < 3600 => $"{(int)age.TotalMinutes} dk önce",
        _ => $"{(int)age.TotalHours} sa önce"
    };
}
