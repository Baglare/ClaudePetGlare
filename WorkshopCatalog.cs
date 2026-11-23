using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Petglare;

/// <summary>One pet in the online catalog, as listed in index.json.</summary>
internal sealed record CatalogPet(string Id, string Name, string Author, string Created, string Path,
    long Bytes, int Width, int Height, string Sha256)
{
    internal string Origin => WorkshopCatalog.OriginPrefix + Id;
}

/// <summary>
/// The online Workshop: a public GitHub repository (Baglare/Petglare-Workshop). Reading uses
/// plain HTTPS from raw.githubusercontent.com and happens only when the user turned the
/// Workshop on. Everything read is untrusted: sizes are capped, entries that do not check out
/// are skipped, every picture must match its SHA-256 and is decoded with the picture limits
/// and re-encoded before it is stored. Sending a pet goes through the user's browser.
/// </summary>
internal static partial class WorkshopCatalog
{
    internal const string Repository = "Baglare/Petglare-Workshop";
    internal const string OriginPrefix = "workshop:";
    internal const int MaxIndexBytes = 1024 * 1024;
    internal const int MaxPetBytes = 300 * 1024;
    internal const int MaxPets = 2000;
    internal const int MaxSide = 512;

    /// <summary>Where the catalog is read from; tests point this elsewhere.</summary>
    internal static string BaseUrl { get; set; } = $"https://raw.githubusercontent.com/{Repository}/main/";

    /// <summary>Tests replace the network.</summary>
    internal static HttpMessageHandler? Handler { get; set; }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    private static HttpClient Client()
    {
        var http = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Petglare");
        return http;
    }

    /// <summary>The catalog, newest first. Throws when it cannot be read; bad entries are only skipped.</summary>
    internal static async Task<IReadOnlyList<CatalogPet>> LoadAsync(CancellationToken cancel)
    {
        using var http = Client();
        var json = await GetLimitedAsync(http, BaseUrl + "index.json", MaxIndexBytes, cancel);
        return Parse(json);
    }

    internal static IReadOnlyList<CatalogPet> Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != "petglare-catalog-v1"
            || !root.TryGetProperty("pets", out var pets) || pets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Workshop kataloğu tanınmadı.");
        var result = new List<CatalogPet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pets.EnumerateArray())
        {
            if (result.Count >= MaxPets) break;
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Text(item, "id");
            var sha = Text(item, "sha256");
            var path = Text(item, "path");
            if (id is null || !IdPattern().IsMatch(id) || !seen.Add(id)) continue;
            if (path != $"pets/{id}/pet.png" || sha is null || !HashPattern().IsMatch(sha)) continue;
            long bytes = Number(item, "bytes");
            int width = (int)Number(item, "width"), height = (int)Number(item, "height");
            if (bytes is <= 0 or > MaxPetBytes || width is <= 0 or > MaxSide || height is <= 0 or > MaxSide) continue;
            var name = PetLibrary.CleanName(Text(item, "name"));
            var author = PetLibrary.CleanName(Text(item, "author"));
            result.Add(new CatalogPet(id, name, author == "Adsız pet" ? "" : author, Text(item, "created") ?? "", path,
                bytes, width, height, sha));
        }
        return result;
    }

    /// <summary>A pet's picture, only if it is exactly the file the catalog describes.</summary>
    internal static async Task<byte[]> DownloadAsync(CatalogPet pet, CancellationToken cancel)
    {
        using var http = Client();
        var png = await GetLimitedAsync(http, BaseUrl + pet.Path, (int)pet.Bytes, cancel);
        if (png.Length != pet.Bytes || Convert.ToHexStringLower(SHA256.HashData(png)) != pet.Sha256)
            throw new InvalidDataException("İndirilen görsel katalogdakiyle aynı değil.");
        return png;
    }

    /// <summary>Downloads (unless the bytes are at hand), checks and adds a catalog pet to the library.</summary>
    internal static async Task<PetInfo> InstallAsync(CatalogPet pet, byte[]? png, CancellationToken cancel)
    {
        if (png is null || png.Length != pet.Bytes || Convert.ToHexStringLower(SHA256.HashData(png)) != pet.Sha256)
            png = await DownloadAsync(pet, cancel);
        return PetLibrary.ImportImage(png, pet.Name, pet.Origin);
    }

    /// <summary>Catalog ids already in the library (to mark them "added").</summary>
    internal static HashSet<string> Installed() => PetLibrary.List()
        .Select(p => p.Origin).Where(o => o?.StartsWith(OriginPrefix, StringComparison.Ordinal) == true)
        .Select(o => o![OriginPrefix.Length..]).ToHashSet(StringComparer.Ordinal);

    private static async Task<byte[]> GetLimitedAsync(HttpClient http, string url, int limit, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var length && length > limit)
            throw new InvalidDataException("Workshop yanıtı beklenenden büyük.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Workshop yanıtı beklenenden büyük.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : -1;

    // ───────────────────────────── Sending ─────────────────────────────

    /// <summary>The GitHub form for a new pet, with its name filled in.</summary>
    internal static string SubmitUrl(string name) =>
        $"https://github.com/{Repository}/issues/new?template=pet.yml"
        + $"&title={Uri.EscapeDataString("Pet: " + name)}&pet-name={Uri.EscapeDataString(name)}";

    /// <summary>Copies the pet's picture to a folder of its own, ready to drag into the form. Returns the file.</summary>
    internal static string PrepareSubmission(PetInfo pet, string? folder = null)
    {
        folder ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petglare-Gonderim");
        Directory.CreateDirectory(folder);
        var safe = string.Concat(pet.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        var file = System.IO.Path.Combine(folder, (safe.Length == 0 ? "pet" : safe) + ".png");
        File.Copy(pet.ImagePath, file, overwrite: true);
        return file;
    }

    /// <summary>Opens the form in the browser and shows the prepared file in Explorer.</summary>
    internal static void OpenSubmission(PetInfo pet)
    {
        var file = PrepareSubmission(pet);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        Process.Start(new ProcessStartInfo(SubmitUrl(pet.Name)) { UseShellExecute = true });
    }
}
