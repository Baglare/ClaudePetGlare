using System;
using System.Linq;
using System.Windows.Media;

namespace Petglare;

/// <summary>The static mascot shapes, colour palettes and bubble styles a user can pick.</summary>
internal static class Appearance
{
    internal sealed record MascotShape(string Name, string Body, bool Ears);
    internal sealed record Palette(string Name, Color Body, Color Outline, Color Accent, Color Eyes);
    internal sealed record BubbleStyle(string Name, Color Background, Color Border,
        Color Text, Color Dim, Color Done, Color Error, Color Warning);

    internal static readonly MascotShape[] Shapes =
    [
        new("Kedi", "M 10,47 C 7,36 10,17 15,8 C 17,3 21,3 26,8 L 36,19 Q 46,16 55,20 L 68,7 Q 75,1 78,10 "
            + "Q 83,24 78,44 C 87,63 77,82 58,86 L 31,86 C 10,84 2,66 10,47 Z", true),
        new("Damla", "M 45,10 C 58,24 80,34 80,56 C 80,75 65,86 45,86 C 25,86 10,75 10,56 "
            + "C 10,34 32,24 45,10 Z", false),
        new("Hayalet", "M 11,50 C 11,25 27,12 45,12 C 63,12 79,25 79,50 L 79,85 Q 72,79 65,85 "
            + "Q 58,91 51,85 Q 45,79 39,85 Q 32,91 25,85 Q 18,79 11,85 Z", false)
    ];

    internal static readonly Palette[] Palettes =
    [
        new("Lavanta", Rgb(0xC9BDFF), Rgb(0x2D284C), Rgb(0xE995BA), Rgb(0xA9F4D8)),
        new("Nane", Rgb(0xA9EBD3), Rgb(0x1F4A3D), Rgb(0xF5A3C0), Rgb(0xFFE58A)),
        new("Şeftali", Rgb(0xFFC9A8), Rgb(0x5A2F22), Rgb(0xF28C8C), Rgb(0xA9F4D8)),
        new("Gece", Rgb(0x5A5F92), Rgb(0x15172B), Rgb(0x9C8BE8), Rgb(0xFFD36E)),
        new("Kar", Rgb(0xF2F2F7), Rgb(0x3A3A4A), Rgb(0xF4A7C3), Rgb(0x8FD3FF))
    ];

    internal static readonly BubbleStyle[] Bubbles =
    [
        new("Gece", Rgb(0x222642), Rgb(0x66658B), Rgb(0xF5F1FF), Rgb(0xB4AFD6),
            Rgb(0xA9F4D8), Rgb(0xFFBDC5), Rgb(0xFFD98A)),
        new("Koyu", Rgb(0x1C1C21), Rgb(0x4A4A52), Rgb(0xF2F2F2), Rgb(0xA8A8B0),
            Rgb(0x8FE8C4), Rgb(0xFFADB7), Rgb(0xFFD27A)),
        new("Lavanta", Rgb(0x4A3F8C), Rgb(0x9B8FE6), Rgb(0xFFFFFF), Rgb(0xD5CEFF),
            Rgb(0xB8F7DF), Rgb(0xFFC7CE), Rgb(0xFFE29E)),
        new("Açık", Rgb(0xFBFAFF), Rgb(0xC9BDFF), Rgb(0x2A2540), Rgb(0x6E6890),
            Rgb(0x1E7A57), Rgb(0xB3261E), Rgb(0x8A5200))
    ];

    /// <summary>How big one speech bubble is, and how wide the pet window must be to hold it.</summary>
    internal sealed record BubbleMetrics(string Name, double Font, double Height, double Radius,
        double TextWidth, double WindowWidth, double Tick);

    private static readonly BubbleMetrics NormalBubble = new("Normal", 13, 38, 15, 218, 260, 22);

    /// <summary>Quick picks under the sliders; the sliders themselves take any value in range.</summary>
    internal static readonly (string Name, int Percent)[] BubblePresets = [("Küçük", 92), ("Normal", 100), ("Büyük", 115)];
    internal static readonly int[] Opacities = [100, 85, 70, 55];
    internal static readonly int[] MascotSizes = [80, 100, 120, 150];

    /// <summary>The normal bubble scaled to <paramref name="percent"/>.</summary>
    internal static BubbleMetrics Metrics(int percent)
    {
        double k = percent / 100.0;
        var n = NormalBubble;
        return new BubbleMetrics($"%{percent}", Math.Round(n.Font * k, 1), Math.Round(n.Height * k),
            Math.Round(n.Radius * k), Math.Round(n.TextWidth * k), Math.Ceiling(n.WindowWidth * k), Math.Round(n.Tick * k, 1));
    }

    internal static MascotShape Shape(string? name) => Shapes.FirstOrDefault(s => s.Name == name) ?? Shapes[0];
    internal static Palette Colors(string? name) => Palettes.FirstOrDefault(p => p.Name == name) ?? Palettes[0];
    internal static BubbleStyle Bubble(string? name) => Bubbles.FirstOrDefault(b => b.Name == name) ?? Bubbles[0];

    internal static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(uint value) =>
        Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
