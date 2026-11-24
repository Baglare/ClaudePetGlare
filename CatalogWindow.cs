using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petglare;

/// <summary>
/// Browses the online Workshop and adds pets to the library. Opened only while the user has
/// the Workshop turned on; every picture is checked before it is shown or stored.
/// </summary>
internal sealed class CatalogWindow : Window
{
    private const int ShownAtOnce = 60;

    private readonly CancellationTokenSource closing = new();
    private readonly Dictionary<string, byte[]> pictures = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim downloads = new(4);
    private readonly WrapPanel grid = new();
    private readonly TextBox search;
    private readonly TextBlock status;
    private readonly Action<PetInfo>? added;
    private IReadOnlyList<CatalogPet> pets = [];
    private HashSet<string> installed = new(StringComparer.Ordinal);

    internal CatalogWindow(Action<PetInfo>? added = null)
    {
        this.added = added;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Theme.xaml") });
        Title = "Petglare Workshop";
        Icon = TrayIcon.AppIcon;
        Width = 820;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("Canvas");
        Foreground = (Brush)FindResource("Ink");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        // Turning the Workshop off closes this window and stops what it was downloading.
        WorkshopCatalog.Disabled += OnDisabled;
        Closed += (_, _) =>
        {
            WorkshopCatalog.Disabled -= OnDisabled;
            closing.Cancel();
        };

        var heading = new TextBlock { Text = "Workshop", FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = new TextBlock
        {
            Text = "Topluluğun paylaştığı pet'ler. Eklediğin pet bilgisayarına indirilir, doğrulanır ve Ayarlar → Şekil'de görünür.",
            Foreground = (Brush)FindResource("Soft"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        };
        search = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6), Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Ink"),
            CaretBrush = (Brush)FindResource("Ink"), BorderBrush = (Brush)FindResource("Line"), MinWidth = 260
        };
        System.Windows.Automation.AutomationProperties.SetName(search, "Pet ara");
        search.TextChanged += (_, _) => ShowPets();
        var refresh = new Button { Content = "Yenile", Style = (Style)FindResource("Secondary"), Margin = new Thickness(10, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAsync();
        var send = new Button { Content = "Nasıl pet gönderirim?", Style = (Style)FindResource("Quiet"), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Ayarlar → Şekil → kendi pet'ine sağ tık → Workshop'a gönder…" };
        send.Click += (_, _) => MessageBox.Show(this,
            "Ayarlar → Şekil satırında kendi pet'ine sağ tıkla → Workshop'a gönder…\n\n"
            + "Petglare PNG'yi hazırlar ve GitHub'daki formu tarayıcıda açar. Formu göndermek için GitHub hesabı gerekir; "
            + "gönderim herkese açıktır ve bakımcı onaylayınca kataloğa girer.", "Petglare Workshop");
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(refresh);
        right.Children.Add(send);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(search);
        status = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };

        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        var top = new StackPanel();
        top.Children.Add(heading);
        top.Children.Add(intro);
        top.Children.Add(bar);
        top.Children.Add(status);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = layout;
        Loaded += async (_, _) => await LoadAsync();
    }

    private void OnDisabled()
    {
        closing.Cancel();
        if (Dispatcher.CheckAccess()) Close();
        else Dispatcher.BeginInvoke(Close);
    }

    internal async Task LoadAsync()
    {
        status.Text = "Katalog yükleniyor…";
        grid.Children.Clear();
        try
        {
            pets = await WorkshopCatalog.LoadAsync(closing.Token);
            installed = WorkshopCatalog.Installed();
            ShowPets();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or IOException
                                       or InvalidOperationException)
        {
            status.Text = "Workshop'a ulaşılamadı: " + ex.Message;
        }
    }

    internal int Shown => grid.Children.Count;

    private void ShowPets()
    {
        var query = search.Text.Trim();
        var matching = pets.Where(p => query.Length == 0
                || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || p.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        grid.Children.Clear();
        foreach (var pet in matching.Take(ShownAtOnce)) grid.Children.Add(Card(pet));
        status.Text = pets.Count == 0 ? "Katalogda henüz pet yok. İlk gönderen sen ol!"
            : matching.Count == 0 ? "Aramaya uyan pet yok."
            : matching.Count > ShownAtOnce ? $"{matching.Count} pet bulundu; ilk {ShownAtOnce} tanesi gösteriliyor. Aramayı daraltabilirsin."
            : $"{matching.Count} pet";
    }

    private FrameworkElement Card(CatalogPet pet)
    {
        var picture = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var name = new TextBlock { Text = pet.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var author = new TextBlock { Text = pet.Author.Length > 0 ? pet.Author : " ", Foreground = (Brush)FindResource("Muted"),
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        bool have = installed.Contains(pet.Id);
        var add = new Button
        {
            Content = have ? "Eklendi ✓" : "Ekle", Style = (Style)FindResource(have ? "Secondary" : "Primary"),
            IsEnabled = !have, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(add, $"{pet.Name} ekle");
        add.Click += async (_, _) => await Install(pet, add);
        var body = new StackPanel { Width = 150 };
        body.Children.Add(picture);
        body.Children.Add(name);
        body.Children.Add(author);
        body.Children.Add(add);
        _ = LoadPicture(pet, picture);
        return new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(14), Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 12), Child = body, ToolTip = $"{pet.Name}\n{pet.Author}\nCC BY 4.0"
        };
    }

    private async Task LoadPicture(CatalogPet pet, Image target)
    {
        try
        {
            await downloads.WaitAsync(closing.Token);
            byte[] png;
            try
            {
                if (!pictures.TryGetValue(pet.Id, out png!))
                    pictures[pet.Id] = png = await WorkshopCatalog.DownloadAsync(pet, closing.Token);
            }
            finally { downloads.Release(); }
            // Shown through the same size checks as any picture from elsewhere.
            target.Source = PetImage.Decode(new MemoryStream(png, writable: false), 192);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException)
        {
            target.ToolTip = "Görsel yüklenemedi";
        }
    }

    private async Task Install(CatalogPet pet, Button button)
    {
        button.IsEnabled = false;
        button.Content = "Ekleniyor…";
        try
        {
            pictures.TryGetValue(pet.Id, out var png);
            var info = await WorkshopCatalog.InstallAsync(pet, png, closing.Token);
            installed.Add(pet.Id);
            button.Content = "Eklendi ✓";
            button.Style = (Style)FindResource("Secondary");
            status.Text = $"“{info.Name}” eklendi. Ayarlar → Şekil'den seçebilirsin.";
            added?.Invoke(info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            button.IsEnabled = true;
            button.Content = "Ekle";
            status.Text = "Eklenemedi: " + ex.Message;
        }
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int caption = 0x002C1A17;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
