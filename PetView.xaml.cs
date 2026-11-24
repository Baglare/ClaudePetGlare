using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Petglare;

internal enum BubbleTone { Normal, Warning, Attention, Done, Error }

/// <summary>What one speech bubble says.</summary>
internal sealed record BubbleModel(string? Project, string Message, BubbleTone Tone);

/// <summary>
/// The mascot and its speech bubbles. The desktop pet and the settings preview both draw
/// with this view, so what the preview shows is exactly what the desktop will show.
/// </summary>
public partial class PetView : UserControl
{
    private const double MascotHeight = 91;
    private const double MascotBottomMargin = 4;
    private const double BubbleGap = 4;

    private TranslateTransform? tail;
    private FrameworkElement? closest;
    private bool interactive = true;


    public PetView() => InitializeComponent();

    /// <summary>Whether the mascot reacts to the left button (hand cursor). Nothing pops up on hover.</summary>
    internal bool Interactive
    {
        get => interactive;
        set
        {
            interactive = value;
            MascotCanvas.Cursor = value ? Cursors.Hand : Cursors.Arrow;
        }
    }

    /// <summary>Draws only the mascot, for small previews.</summary>
    internal bool MascotOnly
    {
        set
        {
            BubbleStack.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            BubbleRow.Height = value ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }
    }

    /// <summary>The window size that holds the mascot and the configured number of bubbles.</summary>
    internal static Size WindowSize(Preferences prefs)
    {
        var metrics = Appearance.Metrics(prefs.BubblePercent);
        int count = prefs.BubbleCount;
        // 5 above, the bubbles and their gaps, then the tail, minus the overlap with the ears.
        double bubbles = 5 + count * metrics.Height + (count - 1) * BubbleGap + 8 - 3;
        double scale = prefs.MascotSize / 100.0;
        double mascot = MascotHeight + MascotBottomMargin + Math.Ceiling(MascotHeight * Math.Max(0, scale - 1));
        // A large mascot must still fit across the window.
        double width = Math.Max(metrics.WindowWidth, Math.Ceiling(89 * scale) + 12);
        return new Size(width, Math.Ceiling(bubbles + mascot));
    }

    /// <param name="picture">Shown instead of the chosen mascot (the Workshop preview).</param>
    internal void ApplyLook(Preferences prefs, ImageSource? picture = null)
    {
        var custom = picture ?? CustomPicture(prefs.Mascot);
        Vector.Visibility = custom is null ? Visibility.Visible : Visibility.Collapsed;
        Custom.Visibility = custom is null ? Visibility.Collapsed : Visibility.Visible;
        CustomImage.Source = custom;
        var shape = Appearance.Shape(prefs.Mascot);
        var palette = Appearance.Colors(prefs.Palette);
        Body.Data = Geometry.Parse(shape.Body);
        Body.Fill = Appearance.Brush(palette.Body);
        Body.Stroke = Appearance.Brush(palette.Outline);
        Ears.Visibility = shape.Ears ? Visibility.Visible : Visibility.Collapsed;
        Ears.Fill = Cheeks.Stroke = Appearance.Brush(palette.Accent);
        LeftEye.Fill = RightEye.Fill = Appearance.Brush(palette.Eyes);
        SessionBadge.BorderBrush = Appearance.Brush(palette.Body);
        MascotScale.ScaleX = MascotScale.ScaleY = prefs.MascotSize / 100.0;
        Root.Opacity = prefs.OpacityPercent / 100.0;
    }

    private static readonly Dictionary<string, (DateTime Written, ImageSource Image)> pictures = new();

    /// <summary>The picture of a user-made pet, cached until its file changes; null for built-in shapes or a missing pet.</summary>
    internal static ImageSource? CustomPicture(string? mascot)
    {
        if (PetLibrary.Find(PetLibrary.IdOf(mascot)) is not { } pet) return null;
        DateTime written;
        try { written = System.IO.File.GetLastWriteTimeUtc(pet.ImagePath); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
        if (pictures.TryGetValue(pet.Id, out var cached) && cached.Written == written) return cached.Image;
        if (PetLibrary.LoadImage(pet) is not { } image) return null;
        pictures[pet.Id] = (written, image);
        return image;
    }

    /// <summary>Draws the bubbles, most important first, and the small badges on the mascot.</summary>
    internal void Render(Preferences prefs, IReadOnlyList<BubbleModel> bubbles, bool badge, int active)
    {
        var style = Appearance.Bubble(prefs.Bubble);
        var metrics = Appearance.Metrics(prefs.BubblePercent);
        BubbleStack.Children.Clear();
        tail = null;
        closest = null;

        int count = Math.Min(bubbles.Count, prefs.BubbleCount);
        for (int i = count - 1; i >= 0; i--)
        {
            bool nearest = i == 0;
            var element = MakeBubble(bubbles[i], prefs, style, metrics, nearest);
            if (!nearest) element.Margin = new Thickness(0, 0, 0, BubbleGap);
            BubbleStack.Children.Add(element);
            if (nearest) closest = element;
        }

        DoneSpark.Visibility = badge && count == 0 && prefs.ShowSpark != false
            ? Visibility.Visible : Visibility.Collapsed;
        SessionBadge.Visibility = active >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = active.ToString();
        UpdateBubblePlacement();
    }

    private FrameworkElement MakeBubble(BubbleModel model, Preferences prefs, Appearance.BubbleStyle style,
        Appearance.BubbleMetrics metrics, bool nearest)
    {
        var background = Appearance.Brush(style.Background);
        var dim = Appearance.Brush(style.Dim);
        bool done = model.Tone == BubbleTone.Done;
        var color = model.Tone switch
        {
            BubbleTone.Done => style.Done,
            BubbleTone.Error => style.Error,
            BubbleTone.Warning or BubbleTone.Attention => style.Warning,
            _ => style.Text
        };

        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = metrics.TextWidth,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = metrics.Font,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (prefs.ShowProject && model.Project is not null)
        {
            text.Inlines.Add(new Run(model.Project) { Foreground = dim, FontWeight = FontWeights.Normal });
            text.Inlines.Add(new Run(done ? "  " : " · ") { Foreground = dim, FontWeight = FontWeights.Normal });
        }
        text.Inlines.Add(new Run(model.Message)
        {
            Foreground = Appearance.Brush(color),
            FontSize = done ? metrics.Tick : metrics.Font,
            BaselineAlignment = BaselineAlignment.Center
        });

        var border = new Border
        {
            Background = background,
            BorderBrush = Appearance.Brush(model.Tone switch
            {
                BubbleTone.Attention => style.Warning,
                BubbleTone.Error => style.Error,
                _ => style.Border
            }),
            BorderThickness = new Thickness(model.Tone is BubbleTone.Attention or BubbleTone.Error ? 1.5 : 1),
            CornerRadius = new CornerRadius(metrics.Radius),
            MinWidth = metrics.Height + 15,
            Height = metrics.Height,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = text
        };

        var bubble = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        if (nearest)
        {
            border.Margin = new Thickness(0, 0, 0, 8);
            tail = new TranslateTransform();
            bubble.Children.Add(new Path
            {
                Data = Geometry.Parse("M 0,0 L 12,0 L 12,9 Z"),
                Fill = background,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 28, 0),
                RenderTransform = tail
            });
        }
        bubble.Children.Add(border);
        return bubble;
    }

    /// <summary>
    /// Keeps the bubbles over the mascot when the mascot moves inside the window, and points
    /// the tail of the nearest bubble at it.
    /// </summary>
    internal void UpdateBubblePlacement()
    {
        double stackWidth = BubbleStack.ActualWidth;
        double availableShift = Math.Max(0, ActualWidth - stackWidth - BubbleStack.Margin.Left - BubbleStack.Margin.Right);
        BubbleOffset.X = Math.Max(MascotOffset.X, -availableShift);
        if (tail is null || closest is null) return;
        // The nearest bubble is right-aligned in the stack; measure the tail from its right edge.
        double tailShift = MascotOffset.X - BubbleOffset.X + Mascot.Width * (1 - MascotScale.ScaleX) / 2;
        tail.X = Math.Clamp(tailShift, -Math.Max(0, closest.ActualWidth - 40), 28);
    }

    private void StackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBubblePlacement();
    private void ViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBubblePlacement();

}
