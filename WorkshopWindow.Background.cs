using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Petglare;

/// <summary>
/// Pet Workshop's background removal without the model: a live preview of what the chosen
/// colours and tolerance would erase, applied only when the user confirms.
/// </summary>
public partial class WorkshopWindow
{
    /// <summary>
    /// Above this share of the kept pixels, applying asks first: the subject may be going too.
    /// High on purpose: in photos the background often covers most of the picture.
    /// </summary>
    internal const double WarnShare = 0.9;

    private readonly List<(PetImage.LabColor Lab, Color Rgb)> picked = new();
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool[]? previewRemove;
    private int previewCount, previewKept;
    private Border? previewPanel;
    private TextBlock? previewText, previewWarning;
    private WrapPanel? colorChips;

    /// <summary>Whether a removal preview is on screen (nothing is erased until it is applied).</summary>
    internal bool Previewing => previewRemove is not null;

    internal int PreviewCount => previewCount;

    internal static bool TooMuch(int remove, int kept) => kept > 0 && remove > kept * WarnShare;

    private void AddBackgroundTools()
    {
        var label = new TextBlock { Text = "Arka plan rengi", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 6) };
        Tools.Children.Add(label);
        colorChips = new WrapPanel();
        Tools.Children.Add(colorChips);
        RebuildChips();

        AddButtons(("Önizle: kenardan sil", (_, _) => StartPreview(), true));

        previewText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        previewWarning = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8A)), Visibility = Visibility.Collapsed };
        var apply = new Button { Content = "Uygula", Style = StyleOf("Primary"), Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 6, 0) };
        apply.Click += (_, _) => ApplyPreview();
        var cancel = new Button { Content = "Vazgeç", Style = StyleOf("Small"), Margin = new Thickness(0) };
        cancel.Click += (_, _) => EndPreview("Önizleme kapatıldı");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var body = new StackPanel();
        body.Children.Add(previewText);
        body.Children.Add(previewWarning);
        body.Children.Add(buttons);
        previewPanel = new Border
        {
            Background = (Brush)FindResource("Canvas"), BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 4),
            Visibility = Visibility.Collapsed, Child = body
        };
        Tools.Children.Add(previewPanel);
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); ComputePreview(); };
    }

    /// <summary>The picked background colours as removable swatches; "edges" when none are picked.</summary>
    private void RebuildChips()
    {
        if (colorChips is null) return;
        colorChips.Children.Clear();
        if (picked.Count == 0)
            colorChips.Children.Add(new TextBlock
            {
                Text = "Otomatik: kenarlardaki renkler. “Renk seç” aracıyla kendin seçebilirsin.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });
        foreach (var color in picked.ToList())
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"#{color.Rgb.R:X2}{color.Rgb.G:X2}{color.Rgb.B:X2} · kaldırmak için tıkla",
                Template = SwatchTemplate(color.Rgb)
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "Rengi kaldır");
            swatch.Click += (_, _) =>
            {
                picked.Remove(color);
                RebuildChips();
                SchedulePreview();
            };
            colorChips.Children.Add(swatch);
        }
        if (picked.Count > 0)
        {
            var clear = new Button { Content = "Otomatiğe dön", Style = StyleOf("Small"), Margin = new Thickness(0, 0, 0, 6) };
            clear.Click += (_, _) => { picked.Clear(); RebuildChips(); SchedulePreview(); };
            colorChips.Children.Add(clear);
        }
    }

    private static ControlTemplate SwatchTemplate(Color color)
    {
        var ellipse = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        ellipse.SetValue(System.Windows.Shapes.Shape.FillProperty, new SolidColorBrush(color));
        ellipse.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White);
        ellipse.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        return new ControlTemplate(typeof(Button)) { VisualTree = ellipse };
    }

    /// <summary>Adds the colour under (x, y) as a background colour and shows what it would remove.</summary>
    internal void PickColor(int x, int y)
    {
        if (image is null || x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
        var (r, g, b) = image.RgbAt(x, y);
        var lab = image.SampleColor(x, y);
        if (picked.Any(p => p.Lab.DistanceTo(lab) < 1)) return;
        if (picked.Count >= 6) picked.RemoveAt(0);
        picked.Add((lab, Color.FromRgb(r, g, b)));
        RebuildChips();
        if (Previewing) SchedulePreview();
        else StartPreview();
    }

    internal void StartPreview()
    {
        if (image is null) return;
        ComputePreview(force: true);
    }

    /// <summary>Recomputes the preview soon (after the slider settles); does nothing when no preview is shown.</summary>
    private void SchedulePreview()
    {
        if (!Previewing) return;
        previewTimer.Stop();
        previewTimer.Start();
    }

    private void ComputePreview(bool force = false)
    {
        if (image is null || (!force && !Previewing)) return;
        var colors = picked.Select(p => p.Lab).ToList();
        previewRemove = image.EdgeRemoval(tolerance, colors);
        previewKept = image.KeptCount();
        previewCount = 0;
        for (int i = 0; i < previewRemove.Length; i++) if (previewRemove[i]) previewCount++;
        if (previewText is not null)
            previewText.Text = previewCount == 0
                ? "Bu renk ve toleransla kenardan silinecek piksel yok. Toleransı artır veya bir renk seç."
                : $"Pembe alanlar silinecek: {previewCount:N0} piksel (görünen kısmın %{100.0 * previewCount / Math.Max(1, previewKept):0}). "
                  + "Tolerans değiştikçe güncellenir.";
        bool tooMuch = TooMuch(previewCount, previewKept);
        if (previewWarning is not null)
        {
            previewWarning.Text = "Görselin büyük kısmı silinecek; ön plan da gidiyor olabilir. Toleransı düşür, "
                                  + "renk seç veya “Koru” fırçasıyla korunacak yeri boya.";
            previewWarning.Visibility = tooMuch ? Visibility.Visible : Visibility.Collapsed;
        }
        if (previewPanel is not null && previewPanel.Visibility != Visibility.Visible)
        {
            previewPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(previewPanel.BringIntoView, DispatcherPriority.Loaded);
        }
        Refresh(new Int32Rect(0, 0, image.Width, image.Height));
    }

    /// <summary>Erases what the preview shows, as one undoable step.</summary>
    internal void ApplyPreview()
    {
        if (image is null || previewRemove is not { } remove) return;
        if (previewCount == 0)
        {
            ShowStatus("Silinecek piksel yok");
            return;
        }
        if (TooMuch(previewCount, previewKept)
            && !Confirm($"Görünen kısmın %{100.0 * previewCount / Math.Max(1, previewKept):0}'i silinecek. Yine de uygulansın mı?"))
            return;
        previewRemove = null;
        if (previewPanel is not null) previewPanel.Visibility = Visibility.Collapsed;
        Apply(i => i.Erase(remove) > 0, "Arka plan silindi; Ctrl+Z ile geri alabilirsin", "Silinecek piksel yok");
        Refresh(new Int32Rect(0, 0, image.Width, image.Height));
    }

    private void EndPreview(string? message = null)
    {
        if (!Previewing) return;
        previewTimer.Stop();
        previewRemove = null;
        if (previewPanel is not null) previewPanel.Visibility = Visibility.Collapsed;
        if (image is not null) Refresh(new Int32Rect(0, 0, image.Width, image.Height));
        if (message is not null) ShowStatus(message);
    }
}
