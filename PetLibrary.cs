using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Petglare;

/// <summary>A pet the user made or imported.</summary>
internal sealed record PetInfo(string Id, string Name, DateTime Created, string Folder)
{
    internal string ImagePath => Path.Combine(Folder, PetLibrary.ImageFile);
    internal string Key => PetLibrary.Prefix + Id;
    internal bool Editable => File.Exists(Path.Combine(Folder, PetLibrary.SourceFile))
                              && File.Exists(Path.Combine(Folder, PetLibrary.MaskFile));
}

/// <summary>
/// The user's own pets, one folder each under %LOCALAPPDATA%\Petglare\Pets:
/// pet.png (the finished pet), pet.json (name and checksum) and, for pets made here,
/// source.png + mask.png so they can be edited again without loss.
/// </summary>
internal static class PetLibrary
{
    internal const string Prefix = "custom:";
    internal const string ImageFile = "pet.png";
    internal const string InfoFile = "pet.json";
    internal const string SourceFile = "source.png";
    internal const string MaskFile = "mask.png";
    internal const string Extension = ".petglare";
    internal const string Format = "petglare-pet-v1";
    /// <summary>Limits for a pet file from someone else.</summary>
    internal const int MaxImportBytes = 2 * 1024 * 1024;
    internal const int MaxNameLength = 40;

    /// <summary>Where pets live; tests point this at a temporary folder.</summary>
    internal static string Root { get; set; } = Path.Combine(Preferences.Folder, "Pets");

    internal static event Action? Changed;

    private sealed class Meta
    {
        public string Format { get; set; } = PetLibrary.Format;
        public string Name { get; set; } = "";
        public DateTime Created { get; set; }
        public string Sha256 { get; set; } = "";
    }

    internal static bool IsCustom(string? mascot) => mascot?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    internal static string? IdOf(string? mascot) => IsCustom(mascot) ? mascot![Prefix.Length..] : null;

    internal static IReadOnlyList<PetInfo> List()
    {
        if (!Directory.Exists(Root)) return [];
        var pets = new List<PetInfo>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
            if (Find(Path.GetFileName(folder)) is { } pet) pets.Add(pet);
        return pets.OrderBy(p => p.Created).ToList();
    }

    internal static PetInfo? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || !IsSafeId(id)) return null;
        var folder = Path.Combine(Root, id);
        try
        {
            if (!File.Exists(Path.Combine(folder, ImageFile))) return null;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(Path.Combine(folder, InfoFile)));
            return meta is null ? null : new PetInfo(id, CleanName(meta.Name), meta.Created, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "Adsız pet" : clean;
    }

    /// <summary>Saves a new pet (<paramref name="id"/> null) or overwrites one, keeping what is needed to edit it again.</summary>
    internal static PetInfo Save(string? id, string name, PetImage image)
    {
        var pet = image.ComposePet();
        id ??= NewId();
        if (!IsSafeId(id)) throw new ArgumentException("Geçersiz pet kimliği.");
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var created = Find(id)?.Created ?? DateTime.UtcNow;
        var png = PetImage.Png(pet);
        WriteAtomic(Path.Combine(folder, ImageFile), png);
        WriteAtomic(Path.Combine(folder, SourceFile), image.SourcePng());
        WriteAtomic(Path.Combine(folder, MaskFile), image.MaskPng());
        WriteMeta(folder, new Meta { Name = CleanName(name), Created = created, Sha256 = Hash(png) });
        Changed?.Invoke();
        return Find(id)!;
    }

    internal static void Rename(string id, string name)
    {
        var pet = Find(id) ?? throw new FileNotFoundException("Pet bulunamadı.");
        WriteMeta(pet.Folder, new Meta { Name = CleanName(name), Created = pet.Created, Sha256 = Hash(File.ReadAllBytes(pet.ImagePath)) });
        Changed?.Invoke();
    }

    internal static void Delete(string id)
    {
        if (Find(id) is not { } pet) return;
        Directory.Delete(pet.Folder, recursive: true);
        Changed?.Invoke();
    }

    /// <summary>A shareable .petglare file: a zip holding only pet.png and pet.json.</summary>
    internal static void Export(string id, string path)
    {
        var pet = Find(id) ?? throw new FileNotFoundException("Pet bulunamadı.");
        var png = File.ReadAllBytes(pet.ImagePath);
        var temporary = path + ".tmp";
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry(ImageFile, CompressionLevel.NoCompression).Open()) entry.Write(png);
            using var info = zip.CreateEntry(InfoFile).Open();
            JsonSerializer.Serialize(info, new Meta { Name = pet.Name, Created = pet.Created, Sha256 = Hash(png) },
                new JsonSerializerOptions { WriteIndented = true });
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Adds a pet from a .petglare file (or a plain picture). Only the picture is taken, after
    /// it decodes, fits the size limits and is re-encoded; nothing else in the file is used.
    /// </summary>
    internal static PetInfo Import(string path)
    {
        if (new FileInfo(path).Length > MaxImportBytes * 2) throw new InvalidDataException("Dosya çok büyük.");
        string name = Path.GetFileNameWithoutExtension(path);
        byte[] png;
        if (Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 4) throw new InvalidDataException("Pet dosyasında beklenmeyen içerik var.");
            var image = zip.GetEntry(ImageFile) ?? throw new InvalidDataException("Pet dosyasında pet.png yok.");
            if (image.Length > MaxImportBytes) throw new InvalidDataException("Pet görseli çok büyük.");
            png = ReadAll(image);
            if (zip.GetEntry(InfoFile) is { Length: < 16 * 1024 } info)
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<Meta>(ReadAll(info));
                    if (meta is { Format: Format }) name = meta.Name;
                }
                catch (JsonException) { }
            }
        }
        else
        {
            png = File.ReadAllBytes(path);
            if (png.Length > MaxImportBytes) throw new InvalidDataException("Görsel çok büyük.");
        }

        BitmapSource decoded;
        try { decoded = PetImage.Decode(new MemoryStream(png)); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Görsel okunamadı.");
        }
        if (decoded.PixelWidth > 4096 || decoded.PixelHeight > 4096) throw new InvalidDataException("Görsel çok büyük.");
        var picture = PetImage.FromBitmap(decoded, PetImage.EditSide);
        return Save(null, name, picture);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>The finished picture, fully read so the file stays free; null if it is missing or broken.</summary>
    internal static BitmapSource? LoadImage(PetInfo pet)
    {
        try { return PetImage.Decode(pet.ImagePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    internal static PetImage? LoadForEditing(PetInfo pet)
    {
        try
        {
            return pet.Editable
                ? PetImage.FromParts(Path.Combine(pet.Folder, SourceFile), Path.Combine(pet.Folder, MaskFile))
                : PetImage.FromFile(pet.ImagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void WriteMeta(string folder, Meta meta) =>
        WriteAtomic(Path.Combine(folder, InfoFile),
            JsonSerializer.SerializeToUtf8Bytes(meta, new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteAtomic(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }
}
