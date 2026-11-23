using System;
using System.IO;
using System.Text.Json;

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

    // Behaviour
    public bool? ShowSpark { get; set; }
    public bool ShowProject { get; set; } = true;
    public bool SnapToEdges { get; set; } = true;
    public bool HideInFullScreen { get; set; } = true;
    public bool Hidden { get; set; }

    // Notifications
    public bool ShowToasts { get; set; } = true;
    public bool NotifyAttention { get; set; } = true;
    public bool NotifyComplete { get; set; } = true;
    public bool NotifyError { get; set; } = true;
    public bool PlaySound { get; set; }
    public bool QuietWhenClaudeFocused { get; set; } = true;

    // Appearance
    public string Mascot { get; set; } = "Kedi";
    public string Palette { get; set; } = "Lavanta";
    public string Bubble { get; set; } = "Gece";
    public int Opacity { get; set; } = 100;
    public int FontSize { get; set; } = 13;

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
