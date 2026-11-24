using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Petglare;

/// <summary>Per-user settings, stored next to the saved position (older files stay readable).</summary>
internal sealed class Preferences
{
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petglare");
    private static readonly string FilePath = Path.Combine(Folder, "position.json");

    // Position and size
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Size { get; set; }
    public string? Monitor { get; set; }
    public bool Hidden { get; set; }

    // Staying on screen
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>Off by default: most people want the pet over a borderless game. Saved choices are kept.</summary>
    public bool HideInFullScreen { get; set; }
    /// <summary>"Açık", "Tam ekranda kapalı" or "Kapalı".</summary>
    public string LeftClick { get; set; } = LeftClickOn;
    public bool SnapToEdges { get; set; } = true;

    // Bubbles
    public string Bubble { get; set; } = "Gece";
    /// <summary>Bubble size in percent of the normal bubble (85–130).</summary>
    public int? BubbleScale { get; set; }
    /// <summary>Older files: "Küçük", "Normal" or "Büyük". Read once, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BubbleSize { get; set; }
    public int MaxBubbles { get; set; } = 2;
    public bool ShowProject { get; set; } = true;
    public bool? ShowSpark { get; set; }

    // Notifications
    public bool ShowToasts { get; set; } = true;
    public bool NotifyAttention { get; set; } = true;
    public bool NotifyComplete { get; set; } = true;
    public bool NotifyError { get; set; } = true;
    public bool PlaySound { get; set; }
    public bool QuietWhenClaudeFocused { get; set; } = true;

    // Mascot
    public string Mascot { get; set; } = "Kedi";
    public string Palette { get; set; } = "Lavanta";
    public int Opacity { get; set; } = 100;

    internal const string LeftClickOn = "Açık";
    internal const string LeftClickFullScreenOff = "Tam ekranda kapalı";
    internal const string LeftClickOff = "Kapalı";

    internal const int MinMascotSize = 60, MaxMascotSize = 200;
    internal const int MinOpacity = 30;
    internal const int MinBubbleScale = 85, MaxBubbleScale = 130;

    internal int MascotSize => Math.Clamp(Size ?? 100, MinMascotSize, MaxMascotSize);
    internal int OpacityPercent => Math.Clamp(Opacity, MinOpacity, 100);
    internal int BubblePercent => Math.Clamp(BubbleScale ?? BubbleSize switch
    {
        "Küçük" => 92,
        "Büyük" => 115,
        _ => 100
    }, MinBubbleScale, MaxBubbleScale);

    /// <summary>Sets the bubble size and drops the old named size.</summary>
    internal void SetBubbleScale(int percent)
    {
        BubbleScale = Math.Clamp(percent, MinBubbleScale, MaxBubbleScale);
        BubbleSize = null;
    }
    internal int BubbleCount => Math.Clamp(MaxBubbles, 1, 3);

    internal Preferences Clone() =>
        JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(this))!;

    /// <summary>Takes every setting from <paramref name="other"/> except where the pet stands.</summary>
    internal void CopySettingsFrom(Preferences other)
    {
        var (x, y, monitor, hidden) = (X, Y, Monitor, Hidden);
        foreach (var property in typeof(Preferences).GetProperties())
            if (property.CanWrite) property.SetValue(this, property.GetValue(other));
        (X, Y, Monitor, Hidden) = (x, y, monitor, hidden);
    }

    internal static Preferences Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new Preferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Preferences();
        }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
