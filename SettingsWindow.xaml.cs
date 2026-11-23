using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Petglare;

/// <summary>
/// Every lasting choice in one place. Changes go to a draft that the preview draws;
/// the desktop pet takes them only on Apply.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Preferences current;
    private readonly Action<Preferences> apply;
    private readonly Action<Preferences> sampleNotification;
    private readonly Action moveToCorner;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Preferences draft;
    private bool autoStart;
    private bool appliedAutoStart;
    private string previewState = "Çoklu";
    private int group;

    private static readonly string[] PreviewStateNames = ["Çalışırken", "Onay", "Bitti", "Parıltı", "Çoklu"];

    internal SettingsWindow(Preferences current, Action<Preferences> apply, Action<Preferences> sampleNotification,
        Action moveToCorner)
    {
        this.current = current;
        this.apply = apply;
        this.sampleNotification = sampleNotification;
        this.moveToCorner = moveToCorner;
        draft = current.Clone();
        appliedAutoStart = autoStart = SafeAutoStart();
        InitializeComponent();
        Preview.Interactive = false;
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); Status.Text = ""; };
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Build();
    }

    // ───────────────────────────── Layout ─────────────────────────────

    private void Build()
    {
        Sections.Children.Clear();
        PreviewStates.Children.Clear();
        string states = NewGroup();
        foreach (var name in PreviewStateNames)
        {
            var option = Segment(name, states, name == previewState, () => { previewState = name; RefreshPreview(); });
            option.Padding = new Thickness(10, 6, 10, 6);
            if (name == "Çoklu") option.ToolTip = "Birden çok sohbet aynı anda çalışırken";
            PreviewStates.Children.Add(option);
        }

        Section("Maskot", first: true);
        Row("Şekil", null, Tiles(Appearance.Shapes.Select(s => (s.Name, (UIElement)ShapeThumbnail(s.Name))),
            draft.Mascot, v => draft.Mascot = v));
        Row("Renk", null, Swatches());
        Row("Boyut", "Maskotun üzerindeki − / + düğmeleriyle de değiştirebilirsin.",
            Segments(Appearance.MascotSizes.Select(s => ($"%{s}", s)), draft.MascotSize, v => draft.Size = v));
        Row("Saydamlık", null, Segments(Appearance.Opacities.Select(o => (o == 100 ? "Opak" : $"%{o}", o)),
            draft.Opacity, v => draft.Opacity = v));

        Section("Balonlar");
        Row("Stil", null, Tiles(Appearance.Bubbles.Select(b => (b.Name, (UIElement)BubbleThumbnail(b))),
            draft.Bubble, v => draft.Bubble = v));
        Row("Boyut", null, Segments(Appearance.BubbleSizes.Select(m => (m.Name, m.Name)),
            draft.BubbleSize, v => draft.BubbleSize = v));
        Row("Aynı anda gösterilen sohbet",
            "Birden fazla sohbet çalışırken balonlar alt alta dizilir; en önemlisi maskota en yakın durur.",
            Segments(new[] { 1, 2, 3 }.Select(n => (n.ToString(), n)), draft.BubbleCount, v => draft.MaxBubbles = v));
        Switch("Proje adını göster", "Balon “VoxForge · Dosya düzenleniyor” gibi başlar.",
            draft.ShowProject, v => draft.ShowProject = v);
        Switch("Tamamlanma parıltısı", "Yanıt bitince maskotun yanında kalır; Claude’a geçince kaybolur.",
            draft.ShowSpark != false, v => draft.ShowSpark = v);

        Section("Ekranda kalma");
        Switch("Her zaman en üstte", "Kenarlıksız pencere modundaki oyunlarda da üstte kalır. Özel tam ekran "
            + "modundaki oyunların üstüne Windows hiçbir pencereyi çizdirmez.",
            draft.AlwaysOnTop, v => draft.AlwaysOnTop = v);
        Switch("Tam ekranda gizle", "Aynı ekranda tam ekran bir uygulama açıkken maskot saklanır.",
            draft.HideInFullScreen, v => draft.HideInFullScreen = v);
        Row("Sol tık", "Kapalıyken sol tık Claude’u açmaz, maskotu taşımaz ve boyutunu değiştirmez. "
            + "Menü için sağ tık her zaman çalışır.",
            Segments(new[] { Preferences.LeftClickOn, Preferences.LeftClickFullScreenOff, Preferences.LeftClickOff }
                .Select(m => (m, m)), draft.LeftClick, v => draft.LeftClick = v));
        Switch("Ekran kenarlarına yasla", "Kenara yakın bırakınca maskot kenara oturur.",
            draft.SnapToEdges, v => draft.SnapToEdges = v);
        Switch("Windows açılırken başlat", null, autoStart, v => autoStart = v);
        var corner = new Button { Content = "Sağ alt köşeye taşı", Style = StyleOf("Secondary"), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left };
        corner.Click += (_, _) => { moveToCorner(); ShowStatus("Maskot sağ alt köşeye taşındı"); };
        Sections.Children.Add(corner);

        Section("Bildirimler");
        Switch("Windows bildirimi göster", null, draft.ShowToasts, v => draft.ShowToasts = v);
        Switch("Kısa ses çal", null, draft.PlaySound, v => draft.PlaySound = v);
        Switch("Onay veya yanıt beklediğinde", null, draft.NotifyAttention, v => draft.NotifyAttention = v);
        Switch("Uzun bir yanıt bittiğinde", "On saniyeden uzun sürenler için.", draft.NotifyComplete,
            v => draft.NotifyComplete = v);
        Switch("Hata olduğunda", null, draft.NotifyError, v => draft.NotifyError = v);
        Switch("Claude öndeyken sessiz kal", "Claude Desktop penceresine bakarken bildirim gelmez.",
            draft.QuietWhenClaudeFocused, v => draft.QuietWhenClaudeFocused = v);
        var sample = new Button { Content = "Örnek bildirim gönder", Style = StyleOf("Secondary"),
            Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        sample.Click += (_, _) =>
        {
            if (!draft.ShowToasts && !draft.PlaySound) ShowStatus("Bildirim ve ses ikisi de kapalı");
            else sampleNotification(draft);
        };
        Sections.Children.Add(sample);

        RefreshPreview();
    }

    private void Section(string title, bool first = false) => Sections.Children.Add(new TextBlock
    {
        Text = title,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, first ? 2 : 30, 0, 4)
    });

    /// <summary>A label (with an optional hint) above its control.</summary>
    private void Row(string title, string? hint, UIElement control)
    {
        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (hint is not null) row.Children.Add(Hint(hint));
        if (control is FrameworkElement element) element.Margin = new Thickness(0, 8, 0, 0);
        row.Children.Add(control);
        Sections.Children.Add(row);
    }

    /// <summary>A label and hint on the left, a switch on the right.</summary>
    private void Switch(string title, string? hint, bool value, Action<bool> set)
    {
        var box = new CheckBox { Style = StyleOf("Switch"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        box.Click += (_, _) => { set(box.IsChecked == true); Changed(); };

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (hint is not null) text.Children.Add(Hint(hint));

        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, box)) return;
            box.IsChecked = box.IsChecked != true;
            set(box.IsChecked == true);
            Changed();
        };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(text);
        Sections.Children.Add(row);
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = (Brush)FindResource("Muted"), FontSize = 12,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 440,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private UIElement Segments<T>(IEnumerable<(string Name, T Value)> options, T selected, Action<T> set)
    {
        string name = NewGroup();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var captured = value;
            panel.Children.Add(Segment(label, name, EqualityComparer<T>.Default.Equals(value, selected),
                () => { set(captured); Changed(); }));
        }
        return new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Child = panel
        };
    }

    private RadioButton Segment(string label, string group, bool selected, Action chosen)
    {
        var option = new RadioButton { Content = label, GroupName = group, IsChecked = selected, Style = StyleOf("Segment") };
        option.Checked += (_, _) => chosen();
        return option;
    }

    private UIElement Tiles(IEnumerable<(string Name, UIElement Picture)> options, string selected, Action<string> set)
    {
        string name = NewGroup();
        var panel = new WrapPanel();
        foreach (var (label, picture) in options)
        {
            var option = new RadioButton
            {
                Content = picture, Tag = label, GroupName = name, IsChecked = label == selected,
                Style = StyleOf("Tile"), Margin = new Thickness(0, 0, 10, 6)
            };
            System.Windows.Automation.AutomationProperties.SetName(option, label);
            option.Checked += (_, _) => { set(label); Changed(); };
            panel.Children.Add(option);
        }
        return panel;
    }

    private UIElement Swatches()
    {
        string name = NewGroup();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Foreground = (Brush)FindResource("Soft"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), Text = Appearance.Colors(draft.Palette).Name };
        foreach (var palette in Appearance.Palettes)
        {
            var option = new RadioButton
            {
                GroupName = name, IsChecked = palette.Name == Appearance.Colors(draft.Palette).Name,
                Style = StyleOf("Swatch"), ToolTip = palette.Name,
                Background = Appearance.Brush(palette.Body), BorderBrush = Appearance.Brush(palette.Outline)
            };
            System.Windows.Automation.AutomationProperties.SetName(option, palette.Name);
            option.Checked += (_, _) =>
            {
                draft.Palette = palette.Name;
                label.Text = palette.Name;
                RefreshThumbnails();
                Changed();
            };
            panel.Children.Add(option);
        }
        panel.Children.Add(label);
        return panel;
    }

    private readonly List<PetView> thumbnails = new();

    private UIElement ShapeThumbnail(string shape)
    {
        var view = new PetView { MascotOnly = true, Interactive = false, IsHitTestVisible = false };
        view.Tag = shape;
        thumbnails.Add(view);
        PaintThumbnail(view);
        return new Viewbox { Child = view, Width = 58, Height = 58 };
    }

    private void PaintThumbnail(PetView view)
    {
        var look = draft.Clone();
        look.Mascot = (string)view.Tag;
        look.Size = 100;
        look.Opacity = 100;
        view.ApplyLook(look);
    }

    private void RefreshThumbnails()
    {
        foreach (var view in thumbnails) PaintThumbnail(view);
    }

    private static UIElement BubbleThumbnail(Appearance.BubbleStyle style)
    {
        var text = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12 };
        text.Inlines.Add(new System.Windows.Documents.Run("Aa ") { Foreground = Appearance.Brush(style.Dim) });
        text.Inlines.Add(new System.Windows.Documents.Run("✓") { Foreground = Appearance.Brush(style.Done) });
        return new Border
        {
            Background = Appearance.Brush(style.Background), BorderBrush = Appearance.Brush(style.Border),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 5, 10, 5), Child = text
        };
    }

    private string NewGroup() => $"g{group++}";

    private Style StyleOf(string key) => (Style)FindResource(key);

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (element == container) return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    // ───────────────────────────── Preview ─────────────────────────────

    private void RefreshPreview()
    {
        var size = PetView.WindowSize(draft);
        Preview.Width = size.Width;
        Preview.Height = size.Height;
        Preview.ApplyLook(draft);

        var bubbles = new List<BubbleModel>();
        bool badge = false;
        int active = 1;
        switch (previewState)
        {
            case "Çalışırken":
                bubbles.Add(new("VoxForge", "Program.cs düzenleniyor", BubbleTone.Normal));
                break;
            case "Onay":
                bubbles.Add(new("VoxForge", "Onay bekleniyor", BubbleTone.Attention));
                break;
            case "Bitti":
                bubbles.Add(new("VoxForge", "✓", BubbleTone.Done));
                active = 0;
                break;
            case "Parıltı":
                badge = true;
                active = 0;
                break;
            default:
                bubbles.Add(new("VoxForge", "Onay bekleniyor", BubbleTone.Attention));
                bubbles.Add(new("Atlas", "Dosyalar aranıyor", BubbleTone.Normal));
                bubbles.Add(new("Notlar", "Claude düşünüyor", BubbleTone.Normal));
                active = 3;
                break;
        }
        Preview.Render(draft, bubbles, badge, active);
    }

    // ───────────────────────────── Apply ─────────────────────────────

    private void Changed()
    {
        RefreshPreview();
        UpdateApplyButton();
    }

    private bool Dirty
    {
        get
        {
            var mine = current.Clone();
            mine.CopySettingsFrom(draft);
            return JsonSerializer.Serialize(mine) != JsonSerializer.Serialize(current) || autoStart != appliedAutoStart;
        }
    }

    private void UpdateApplyButton()
    {
        bool dirty = Dirty;
        ApplyButton.IsEnabled = dirty;
        if (dirty)
        {
            statusTimer.Stop();
            Status.Text = "Uygulanmamış değişiklikler var";
        }
        else if (!statusTimer.IsEnabled) Status.Text = "";
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        apply(draft.Clone());
        if (autoStart != appliedAutoStart)
        {
            try
            {
                AutoStart.Set(autoStart);
                appliedAutoStart = autoStart;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                           or System.IO.IOException)
            {
                ShowStatus("Windows ile başlatma ayarlanamadı");
                return;
            }
        }
        ShowStatus("Uygulandı");
        UpdateApplyButton();
    }

    private void Defaults(object sender, RoutedEventArgs e)
    {
        var defaults = new Preferences();
        draft.CopySettingsFrom(defaults);
        draft.Size = defaults.Size;
        autoStart = false;
        thumbnails.Clear();
        Build();
        UpdateApplyButton();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string text)
    {
        Status.Text = text;
        statusTimer.Stop();
        statusTimer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UpdateApplyButton();
    }

    private static bool SafeAutoStart()
    {
        try { return AutoStart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>Matches the title bar to the window on Windows 11.</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int caption = 0x002C1A17; // COLORREF of #171A2C
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
