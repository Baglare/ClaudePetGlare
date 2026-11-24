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
internal sealed record PetInfo(string Id, string Name, DateTime Created, string Folder, string? Origin = null)
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
    /// <summary>A .petglare file on disk; it holds one small picture and a short JSON.</summary>
    internal const int MaxPackageBytes = MaxImportBytes + 64 * 1024;
    internal const int MaxInfoBytes = 16 * 1024;
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
        /// <summary>Where the pet came from, e.g. "workshop:mor-kedi-12"; null for pets made here.</summary>
        public string? Origin { get; set; }
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
            return meta is null ? null : new PetInfo(id, CleanName(meta.Name), meta.Created, folder,
                meta.Origin is { Length: <= 80 } origin ? origin : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static string CleanName(string? name)
    {
        // Control characters and invisible format characters (e.g. right-to-left overrides that can make a
        // name read as something else) go; the zero-width joiner stays so combined emoji keep working.
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)
            && (c == '‍' || char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "Adsız pet" : clean;
    }

    /// <summary>Saves a new pet (<paramref name="id"/> null) or overwrites one, keeping what is needed to edit it again.</summary>
    internal static PetInfo Save(string? id, string name, PetImage image, string? origin = null)
    {
        var pet = image.ComposePet();
        id ??= NewId();
        if (!IsSafeId(id)) throw new ArgumentException("Geçersiz pet kimliği.");
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var existing = Find(id);
        var created = existing?.Created ?? DateTime.UtcNow;
        origin ??= existing?.Origin;
        var png = PetImage.Png(pet);
        WriteAtomic(Path.Combine(folder, ImageFile), png);
        WriteAtomic(Path.Combine(folder, SourceFile), image.SourcePng());
        WriteAtomic(Path.Combine(folder, MaskFile), image.MaskPng());
        WriteMeta(folder, new Meta { Name = CleanName(name), Created = created, Sha256 = Hash(png), Origin = origin });
        Changed?.Invoke();
        return Find(id)!;
    }

    internal static void Rename(string id, string name)
    {
        var pet = Find(id) ?? throw new FileNotFoundException("Pet bulunamadı.");
        WriteMeta(pet.Folder, new Meta { Name = CleanName(name), Created = pet.Created,
            Sha256 = Hash(File.ReadAllBytes(pet.ImagePath)), Origin = pet.Origin });
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
        string name = Path.GetFileNameWithoutExtension(path);
        byte[] png;
        if (Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(path).Length > MaxPackageBytes) throw new InvalidDataException("Pet dosyası çok büyük.");
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.Entries.Count > 4) throw new InvalidDataException("Pet dosyasında beklenmeyen içerik var.");
                var image = zip.GetEntry(ImageFile) ?? throw new InvalidDataException("Pet dosyasında pet.png yok.");
                // The sizes in the zip directory can lie, so reading also stops at the limit.
                png = ReadLimited(image, MaxImportBytes);
                if (zip.GetEntry(InfoFile) is { } info)
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<Meta>(ReadLimited(info, MaxInfoBytes));
                        if (meta is { Format: Format }) name = meta.Name;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
                }
            }
            catch (Exception ex) when (ex is not InvalidDataException and (IOException or NotSupportedException or ArgumentException))
            {
                throw new InvalidDataException("Pet dosyası okunamadı.", ex);
            }
        }
        else
        {
            if (new FileInfo(path).Length > MaxImportBytes) throw new InvalidDataException("Görsel çok büyük.");
            png = File.ReadAllBytes(path);
        }

        return ImportImage(png, name);
    }

    /// <summary>
    /// Adds a pet from picture bytes from elsewhere (a file, the Workshop catalog). The header is
    /// checked before any pixel is decoded, and the picture is re-encoded before it is stored.
    /// </summary>
    internal static PetInfo ImportImage(byte[] png, string name, string? origin = null)
    {
        if (png.Length > MaxImportBytes) throw new InvalidDataException("Görsel çok büyük.");
        var decoded = PetImage.Decode(new MemoryStream(png, writable: false), PetImage.EditSide);
        var picture = PetImage.FromBitmap(decoded, PetImage.EditSide);
        return Save(null, name, picture, origin);
    }

    /// <summary>Reads a zip entry but never more than <paramref name="limit"/> bytes, whatever its header claims.</summary>
    internal static byte[] ReadLimited(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit || entry.CompressedLength > limit) throw new InvalidDataException("Pet dosyasındaki içerik çok büyük.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Pet dosyasındaki içerik çok büyük.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>The finished picture, fully read so the file stays free; null if it is missing or broken.</summary>
    internal static BitmapSource? LoadImage(PetInfo pet)
    {
        try { return PetImage.Decode(pet.ImagePath, PetImage.EditSide); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidDataException)
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
                                       or FileFormatException or ArgumentException or InvalidDataException)
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
