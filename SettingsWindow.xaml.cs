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
        PetLibrary.Changed += LibraryChanged;
        Closed += (_, _) =>
        {
            PetLibrary.Changed -= LibraryChanged;
            // Closing without applying returns to the saved choice.
            WorkshopCatalog.SetEnabled(current.WorkshopOnline);
        };
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
        Row("Şekil", "Kendi görselinden pet yapmak için + karosuna bas. Kendi pet’ine sağ tıklayınca düzenleyebilir, "
            + "dışa aktarabilir veya silebilirsin.", MascotTiles());
        Row("Renk", null, Swatches());
        customColorHint = Hint("Kendi pet’lerinde renkler görselden gelir.");
        Sections.Children.Add(customColorHint);
        UpdateSwatches();
        Row("Maskot boyutu", "Maskotun üzerindeki − / + düğmeleriyle de değiştirebilirsin.",
            Fine(Preferences.MinMascotSize, Preferences.MaxMascotSize, draft.MascotSize,
                Appearance.MascotSizes.Select(s => ($"%{s}", s)), v => $"%{v}", v => draft.Size = v));
        Row("Saydamlık", null,
            Fine(Preferences.MinOpacity, 100, draft.OpacityPercent,
                Appearance.Opacities.Select(o => (o == 100 ? "Opak" : $"%{o}", o)),
                v => v == 100 ? "Opak" : $"%{v}", v => draft.Opacity = v));

        Section("Balonlar");
        Row("Stil", null, Tiles(Appearance.Bubbles.Select(b => (b.Name, (UIElement)BubbleThumbnail(b))),
            draft.Bubble, v => draft.Bubble = v));
        Row("Balon boyutu", "Yazı ve balon birlikte büyür.",
            Fine(Preferences.MinBubbleScale, Preferences.MaxBubbleScale, draft.BubblePercent,
                Appearance.BubblePresets.Select(p => (p.Name, p.Percent)), v => $"%{v}", draft.SetBubbleScale));
        Row("Aynı anda gösterilen sohbet",
            "Birden fazla sohbet çalışırken balonlar alt alta dizilir; en önemlisi maskota en yakın durur.",
            Segments(new[] { 1, 2, 3 }.Select(n => (n.ToString(), n)), draft.BubbleCount, v => draft.MaxBubbles = v));
        Switch("Proje adını göster", "Balon “VoxForge · Dosya düzenleniyor” gibi başlar.",
            draft.ShowProject, v => draft.ShowProject = v);
        Switch("Tamamlanma parıltısı", "Yanıt bitince maskotun yanında kalır; Claude’a geçince kaybolur.",
            draft.ShowSpark != false, v => draft.ShowSpark = v);

        Section("Workshop");
        var browse = new Button { Content = "Workshop’tan pet bul…", Style = StyleOf("Secondary"), Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = draft.WorkshopOnline };
        Switch("Workshop’a bağlan (internet)",
            "Açıkken Petglare yalnız Workshop kataloğunu ve eklediğin pet görsellerini GitHub’dan (raw.githubusercontent.com) okur. "
            + "Kapalıyken Workshop için internete hiç bağlanmaz. Pet göndermek tarayıcıda GitHub formuyla olur.",
            draft.WorkshopOnline, v =>
            {
                draft.WorkshopOnline = v;
                browse.IsEnabled = v;
                // Takes effect at once: off closes an open catalog and stops its downloads.
                WorkshopCatalog.SetEnabled(v);
            });
        browse.Click += (_, _) => OpenCatalog();
        Sections.Children.Add(browse);

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
        Switch("Taşınan bağlantıları otomatik onar", "Açıkken yalnız artık bulunmayan Petglare.exe yolunu gösteren kendi Claude hook’ları ve Windows başlangıç kaydı otomatik güncellenir.",
            draft.AutoRelocate == true, v => draft.AutoRelocate = v);
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

    /// <summary>
    /// A slider for any value in range, its value on the right, and quick picks below.
    /// A quick pick lights up whenever the slider sits exactly on it.
    /// </summary>
    private UIElement Fine(int min, int max, int value, IEnumerable<(string Name, int Value)> presets,
        Func<int, string> format, Action<int> set)
    {
        var slider = new Slider { Style = StyleOf("Fine"), Minimum = min, Maximum = max, Value = value, Width = 300,
            VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = format(value), FontWeight = FontWeights.SemiBold, MinWidth = 48,
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var picks = new List<(RadioButton Button, int Value)>();
        string name = NewGroup();
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, pick) in presets)
        {
            var captured = pick;
            var chip = Segment(text, name, pick == value, () => slider.Value = captured);
            chip.Padding = new Thickness(10, 4, 10, 4);
            chip.FontSize = 12;
            picks.Add((chip, pick));
            chips.Children.Add(chip);
        }
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            label.Text = format(v);
            foreach (var (button, pick) in picks) button.IsChecked = pick == v;
            System.Windows.Automation.AutomationProperties.SetItemStatus(slider, format(v));
            set(v);
            Changed();
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(slider);
        top.Children.Add(label);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(top);
        panel.Children.Add(new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0), Child = chips
        });
        return panel;
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

    // ───────────────────────────── Mascot choice ─────────────────────────────

    private UIElement? swatches;
    private TextBlock? customColorHint;

    /// <summary>Built-in shapes, the user's own pets, and a tile that opens Pet Workshop.</summary>
    private UIElement MascotTiles()
    {
        string name = NewGroup();
        var panel = new WrapPanel();
        void Add(string value, string label, UIElement picture, PetInfo? pet)
        {
            var option = new RadioButton
            {
                Content = picture, Tag = label, GroupName = name, IsChecked = value == draft.Mascot,
                Style = StyleOf("Tile"), Margin = new Thickness(0, 0, 10, 6), ToolTip = label
            };
            System.Windows.Automation.AutomationProperties.SetName(option, label);
            option.Checked += (_, _) => { draft.Mascot = value; UpdateSwatches(); Changed(); };
            if (pet is not null) option.ContextMenu = PetMenu(pet);
            panel.Children.Add(option);
        }
        foreach (var shape in Appearance.Shapes) Add(shape.Name, shape.Name, ShapeThumbnail(shape.Name), null);
        foreach (var pet in PetLibrary.List()) Add(pet.Key, pet.Name, ShapeThumbnail(pet.Key), pet);

        var add = new RadioButton
        {
            Content = new TextBlock { Text = "+", FontSize = 30, FontWeight = FontWeights.Light, Foreground = (Brush)FindResource("Accent") },
            Tag = "Yeni pet", GroupName = NewGroup(), Style = StyleOf("Tile"), Margin = new Thickness(0, 0, 10, 6),
            ToolTip = "Pet Workshop: kendi görselinden pet yap"
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "Yeni pet");
        add.Checked += (_, _) => { add.IsChecked = false; OpenWorkshop(null); };
        panel.Children.Add(add);

        var import = new Button { Content = "Pet dosyası içe aktar…", Style = StyleOf("Quiet"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 0, 0, 0) };
        import.Click += (_, _) => ImportPet();
        var stack = new StackPanel();
        stack.Children.Add(panel);
        stack.Children.Add(import);
        return stack;
    }

    private ContextMenu PetMenu(PetInfo pet)
    {
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Düzenle…", () => OpenWorkshop(pet));
        Item("Dışa aktar…", () => ExportPet(pet));
        Item("Workshop’a gönder…", () => SubmitPet(pet));
        menu.Items.Add(new Separator());
        Item("Sil", () => DeletePet(pet));
        return menu;
    }

    private void OpenWorkshop(PetInfo? pet)
    {
        var workshop = new WorkshopWindow(pet, made =>
        {
            draft.Mascot = made.Key;
            RebuildKeepingScroll();
            ShowStatus(pet is null ? $"“{made.Name}” eklendi; Uygula ile masaüstüne geçer" : $"“{made.Name}” güncellendi");
        }, draft) { Owner = this };
        workshop.ShowDialog();
    }

    private CatalogWindow? catalog;

    private void OpenCatalog()
    {
        if (!draft.WorkshopOnline) return;
        if (catalog is { IsLoaded: true })
        {
            catalog.Activate();
            return;
        }
        catalog = new CatalogWindow(pet => ShowStatus($"“{pet.Name}” eklendi")) { Owner = this };
        catalog.Closed += (_, _) => catalog = null;
        catalog.Show();
    }

    private void SubmitPet(PetInfo pet)
    {
        if (MessageBox.Show(this,
                $"“{pet.Name}” Petglare Workshop’a gönderilecek.\n\n"
                + "Tarayıcıda GitHub’daki gönderim formu açılır ve pet görseli Gezgin’de seçili olur; görseli forma sürükleyip gönder. "
                + "Bunun için GitHub hesabı gerekir.\n\nGönderim herkese açıktır ve CC BY 4.0 ile paylaşılır; "
                + "bakımcı onaylayınca kataloğa girer. Devam edilsin mi?",
                "Workshop’a gönder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            WorkshopCatalog.OpenSubmission(pet);
            ShowStatus("Form tarayıcıda açıldı");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowStatus("Gönderim hazırlanamadı: " + ex.Message);
        }
    }

    private void ImportPet()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pet dosyası seç",
            Filter = $"Petglare pet|*{PetLibrary.Extension}"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var pet = PetLibrary.Import(dialog.FileName);
            draft.Mascot = pet.Key;
            RebuildKeepingScroll();
            ShowStatus($"“{pet.Name}” içe aktarıldı");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.IO.InvalidDataException or InvalidOperationException)
        {
            ShowStatus("İçe aktarılamadı: " + ex.Message);
        }
    }

    private void ExportPet(PetInfo pet)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Pet’i dışa aktar",
            FileName = string.Concat(pet.Name.Split(System.IO.Path.GetInvalidFileNameChars())) + PetLibrary.Extension,
            Filter = $"Petglare pet|*{PetLibrary.Extension}"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PetLibrary.Export(pet.Id, dialog.FileName);
            ShowStatus("Dışa aktarıldı");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowStatus("Dışa aktarılamadı: " + ex.Message);
        }
    }

    private void DeletePet(PetInfo pet)
    {
        if (MessageBox.Show(this, $"“{pet.Name}” silinsin mi? Bu geri alınamaz.", "Petglare",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (draft.Mascot == pet.Key) draft.Mascot = Appearance.Shapes[0].Name;
            PetLibrary.Delete(pet.Id);
            ShowStatus($"“{pet.Name}” silindi");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowStatus("Silinemedi: " + ex.Message);
        }
    }

    private void LibraryChanged() => Dispatcher.BeginInvoke(RebuildKeepingScroll);

    private void RebuildKeepingScroll()
    {
        var scroller = Sections.Parent as ScrollViewer;
        double offset = scroller?.VerticalOffset ?? 0;
        thumbnails.Clear();
        Build();
        UpdateApplyButton();
        scroller?.ScrollToVerticalOffset(offset);
    }

    /// <summary>Colours belong to the built-in shapes; a user's pet brings its own.</summary>
    private void UpdateSwatches()
    {
        bool custom = PetLibrary.IsCustom(draft.Mascot);
        if (swatches is not null)
        {
            swatches.IsEnabled = !custom;
            swatches.Opacity = custom ? 0.35 : 1;
        }
        if (customColorHint is not null) customColorHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement Swatches()
    {
        string name = NewGroup();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        swatches = panel;
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
