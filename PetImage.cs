using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petglare;

/// <summary>
/// A picture being turned into a pet: the original colours plus a separate alpha mask.
/// Every tool edits only the mask (crop and rotate also move the colours), so an erased
/// area can always be painted back. No UI here, so the tools can be tested headless.
/// </summary>
internal sealed class PetImage
{
    /// <summary>Longest side kept while editing; larger pictures are scaled down on open.</summary>
    internal const int EditSide = 1024;
    /// <summary>Longest side of the saved pet.</summary>
    internal const int PetSide = 512;
    /// <summary>Refused before decoding: larger pictures would need hundreds of MB of memory.</summary>
    internal const long MaxPixels = 40_000_000;
    internal const int MaxSide = 16_384;
    internal const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Goes up on every change, so a result computed for an older state can be recognised.</summary>
    internal long Revision { get; private set; }

    private void Touch() => Revision++;

    internal int Width { get; private set; }
    internal int Height { get; private set; }
    /// <summary>BGRA, straight (not premultiplied) alpha; the source alpha is folded into <see cref="Mask"/>.</summary>
    internal byte[] Pixels { get; private set; }
    /// <summary>One byte per pixel: 0 erased, 255 kept.</summary>
    internal byte[] Mask { get; private set; }

    internal PetImage(int width, int height, byte[] pixels, byte[] mask)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height * 4 || mask.Length != width * height)
            throw new ArgumentException("Bozuk görsel verisi.");
        Width = width;
        Height = height;
        Pixels = pixels;
        Mask = mask;
    }

    // ───────────────────────────── Loading and saving ─────────────────────────────

    /// <summary>Reads any picture WPF can decode, scaled so its longest side is at most <paramref name="maxSide"/>.</summary>
    internal static PetImage FromBitmap(BitmapSource source, int maxSide = EditSide)
    {
        CheckSize(source.PixelWidth, source.PixelHeight);
        source = Fit(source, maxSide);
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var mask = new byte[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = pixels[i * 4 + 3];
        for (int i = 0; i < mask.Length; i++) pixels[i * 4 + 3] = 255;
        return new PetImage(w, h, pixels, mask);
    }

    internal static PetImage FromFile(string path, int maxSide = EditSide)
    {
        using var stream = OpenLimited(path);
        return FromBitmap(Decode(stream, maxSide), maxSide);
    }

    /// <summary>Opens a picture file after checking its size on disk.</summary>
    internal static FileStream OpenLimited(string path)
    {
        var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes)
        {
            stream.Dispose();
            throw new InvalidDataException("Görsel dosyası çok büyük.");
        }
        return stream;
    }

    /// <summary>Rejects pictures whose size alone would exhaust memory.</summary>
    internal static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new InvalidDataException($"Görsel çok büyük ({width}×{height}).");
    }

    /// <summary>Restores a picture saved for editing: colours and mask as separate files.</summary>
    internal static PetImage FromParts(string sourcePath, string maskPath)
    {
        var image = FromFile(sourcePath, EditSide);
        BitmapSource maskSource;
        using (var stream = OpenLimited(maskPath)) maskSource = Decode(stream, EditSide);
        var mask = new FormatConvertedBitmap(maskSource, PixelFormats.Gray8, null, 0);
        if (mask.PixelWidth != image.Width || mask.PixelHeight != image.Height) return image;
        mask.CopyPixels(image.Mask, image.Width, 0);
        return image;
    }

    /// <summary>Decodes fully into memory and closes the file, so it can be replaced later.</summary>
    internal static BitmapSource Decode(string path, int maxSide = int.MaxValue)
    {
        using var stream = OpenLimited(path);
        return Decode(stream, maxSide);
    }

    /// <summary>
    /// Reads only the header first and refuses oversized pictures; a large picture is then
    /// decoded straight to at most <paramref name="maxSide"/> (JPEG and others scale while decoding).
    /// Only the first frame of an animated picture is used.
    /// </summary>
    internal static BitmapSource Decode(Stream stream, int maxSide = int.MaxValue)
    {
        if (stream.CanSeek && stream.Length > MaxFileBytes) throw new InvalidDataException("Görsel dosyası çok büyük.");
        long start = stream.CanSeek ? stream.Position : 0;
        int width, height;
        try
        {
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (header.Frames.Count == 0) throw new InvalidDataException("Görselde kare yok.");
            width = header.Frames[0].PixelWidth;
            height = header.Frames[0].PixelHeight;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or InvalidOperationException or COMException or OverflowException)
        {
            throw new InvalidDataException("Görsel okunamadı.", ex);
        }
        CheckSize(width, height);
        if (stream.CanSeek) stream.Position = start;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (Math.Max(width, height) > maxSide)
            {
                if (width >= height) image.DecodePixelWidth = maxSide;
                else image.DecodePixelHeight = maxSide;
            }
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or InvalidOperationException or COMException or OverflowException)
        {
            throw new InvalidDataException("Görsel okunamadı.", ex);
        }
    }

    private static BitmapSource Fit(BitmapSource source, int maxSide)
    {
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxSide) return source;
        double k = (double)maxSide / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(k, k));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>The picture with the mask applied, as a frozen bitmap.</summary>
    internal BitmapSource Compose()
    {
        var output = new byte[Pixels.Length];
        for (int i = 0, p = 0; i < Mask.Length; i++, p += 4)
        {
            output[p] = Pixels[p];
            output[p + 1] = Pixels[p + 1];
            output[p + 2] = Pixels[p + 2];
            output[p + 3] = Mask[i];
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, output, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The finished pet: trimmed to what is kept and scaled to at most <see cref="PetSide"/>.</summary>
    internal BitmapSource ComposePet()
    {
        var bounds = Bounds();
        if (bounds.IsEmpty) throw new InvalidOperationException("Görselde hiç görünür piksel kalmadı.");
        BitmapSource result = new CroppedBitmap(Compose(), bounds);
        result = Fit(result, PetSide);
        result.Freeze();
        return result;
    }

    internal byte[] MaskPng()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, Mask, Width);
        return Png(bitmap);
    }

    internal byte[] SourcePng()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * 4);
        return Png(bitmap);
    }

    internal static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ───────────────────────────── Undo ─────────────────────────────

    internal sealed record Snapshot(int Width, int Height, byte[] Pixels, byte[] Mask);

    internal Snapshot Save() => new(Width, Height, (byte[])Pixels.Clone(), (byte[])Mask.Clone());

    internal void Restore(Snapshot snapshot)
    {
        Touch();
        Width = snapshot.Width;
        Height = snapshot.Height;
        Pixels = (byte[])snapshot.Pixels.Clone();
        Mask = (byte[])snapshot.Mask.Clone();
    }

    // ───────────────────────────── Brushes ─────────────────────────────

    /// <summary>
    /// One dab of the eraser (<paramref name="erase"/>) or the restore brush. Hardness 1 is a
    /// crisp edge, 0 fades from the centre. Returns whether anything changed.
    /// </summary>
    internal bool Dab(double cx, double cy, double radius, double hardness, bool erase)
    {
        Touch();
        radius = Math.Max(0.5, radius);
        hardness = Math.Clamp(hardness, 0, 1);
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius));
        bool changed = false;
        double inner = radius * hardness;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
            if (d > radius) continue;
            double strength = d <= inner ? 1 : 1 - (d - inner) / Math.Max(0.001, radius - inner);
            int i = y * Width + x;
            int before = Mask[i];
            int after = erase ? (int)Math.Round(before * (1 - strength)) : (int)Math.Round(before + (255 - before) * strength);
            if (after != before) { Mask[i] = (byte)after; changed = true; }
        }
        return changed;
    }

    /// <summary>Dabs along a line so a fast stroke leaves no gaps.</summary>
    internal bool Stroke(Point from, Point to, double radius, double hardness, bool erase)
    {
        double length = (to - from).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, radius / 3)));
        bool changed = false;
        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            changed |= Dab(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, radius, hardness, erase);
        }
        return changed;
    }

    // ───────────────────────────── Colour tools ─────────────────────────────

    /// <summary>Largest per-channel difference between two pixels, 0–255.</summary>
    private int Distance(int a, int b)
    {
        int pa = a * 4, pb = b * 4;
        return Math.Max(Math.Abs(Pixels[pa] - Pixels[pb]),
            Math.Max(Math.Abs(Pixels[pa + 1] - Pixels[pb + 1]), Math.Abs(Pixels[pa + 2] - Pixels[pb + 2])));
    }

    private int Distance(int a, (byte B, byte G, byte R) color)
    {
        int p = a * 4;
        return Math.Max(Math.Abs(Pixels[p] - color.B), Math.Max(Math.Abs(Pixels[p + 1] - color.G), Math.Abs(Pixels[p + 2] - color.R)));
    }

    /// <summary>Tolerance 0–100 to a channel difference.</summary>
    private static int Limit(int tolerance) => (int)Math.Round(Math.Clamp(tolerance, 0, 100) * 2.55);

    /// <summary>
    /// Magic wand: erases the colour under (x, y). Contiguous follows only touching pixels;
    /// otherwise every similar pixel goes. Returns how many pixels were erased.
    /// </summary>
    internal int FloodErase(int x, int y, int tolerance, bool contiguous)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        Touch();
        int seed = y * Width + x, limit = Limit(tolerance), count = 0;
        if (!contiguous)
        {
            for (int i = 0; i < Mask.Length; i++)
                if (Mask[i] != 0 && Distance(i, seed) <= limit) { Mask[i] = 0; count++; }
            return count;
        }
        var color = (Pixels[seed * 4], Pixels[seed * 4 + 1], Pixels[seed * 4 + 2]);
        return Fill(new[] { seed }, i => Distance(i, color) <= limit);
    }

    /// <summary>
    /// Removes a plain background: starts at the picture's edges and erases inward while the
    /// colour stays close to one of the edge's main colours.
    /// </summary>
    internal int EdgeErase(int tolerance)
    {
        Touch();
        int limit = Limit(tolerance);
        var edge = EdgeIndexes().ToArray();
        var colors = MainColors(edge);
        bool Background(int i) => colors.Any(c => Distance(i, c) <= limit);
        return Fill(edge.Where(Background), Background);
    }

    private IEnumerable<int> EdgeIndexes()
    {
        for (int x = 0; x < Width; x++) { yield return x; if (Height > 1) yield return (Height - 1) * Width + x; }
        for (int y = 1; y < Height - 1; y++) { yield return y * Width; if (Width > 1) yield return y * Width + Width - 1; }
    }

    /// <summary>The colours that make up most of the edge (coarse buckets, each at least 10% of it).</summary>
    private List<(byte B, byte G, byte R)> MainColors(int[] edge)
    {
        var buckets = new Dictionary<int, (long B, long G, long R, int N)>();
        foreach (int i in edge)
        {
            if (Mask[i] == 0) continue;
            int p = i * 4;
            int key = (Pixels[p] >> 5) << 6 | (Pixels[p + 1] >> 5) << 3 | Pixels[p + 2] >> 5;
            buckets.TryGetValue(key, out var b);
            buckets[key] = (b.B + Pixels[p], b.G + Pixels[p + 1], b.R + Pixels[p + 2], b.N + 1);
        }
        int total = buckets.Values.Sum(b => b.N);
        return buckets.Values.Where(b => b.N * 10 >= total).OrderByDescending(b => b.N).Take(4)
            .Select(b => ((byte)(b.B / b.N), (byte)(b.G / b.N), (byte)(b.R / b.N))).ToList();
    }

    /// <summary>Erases, four-way, every reachable kept pixel that passes <paramref name="include"/>.</summary>
    private int Fill(IEnumerable<int> seeds, Func<int, bool> include)
    {
        var seen = new bool[Mask.Length];
        var queue = new Queue<int>();
        foreach (int s in seeds)
            if (!seen[s]) { seen[s] = true; queue.Enqueue(s); }
        int count = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (Mask[i] == 0 || !include(i)) continue;
            Mask[i] = 0;
            count++;
            int x = i % Width;
            if (x > 0 && !seen[i - 1]) { seen[i - 1] = true; queue.Enqueue(i - 1); }
            if (x < Width - 1 && !seen[i + 1]) { seen[i + 1] = true; queue.Enqueue(i + 1); }
            if (i >= Width && !seen[i - Width]) { seen[i - Width] = true; queue.Enqueue(i - Width); }
            if (i + Width < Mask.Length && !seen[i + Width]) { seen[i + Width] = true; queue.Enqueue(i + Width); }
        }
        return count;
    }

    // ───────────────────────────── Edges ─────────────────────────────

    /// <summary>Softens the mask edge with a box blur of <paramref name="radius"/> pixels (0–8).</summary>
    internal void Feather(int radius)
    {
        radius = Math.Clamp(radius, 0, 8);
        if (radius == 0) return;
        Touch();
        var temp = new byte[Mask.Length];
        BlurPass(Mask, temp, radius, horizontal: true);
        BlurPass(temp, Mask, radius, horizontal: false);
    }

    private void BlurPass(byte[] from, byte[] to, int r, bool horizontal)
    {
        int lines = horizontal ? Height : Width, length = horizontal ? Width : Height;
        for (int line = 0; line < lines; line++)
        {
            int Index(int k) => horizontal ? line * Width + k : k * Width + line;
            int sum = 0, n = 0;
            for (int k = 0; k <= Math.Min(r, length - 1); k++) { sum += from[Index(k)]; n++; }
            for (int k = 0; k < length; k++)
            {
                to[Index(k)] = (byte)(sum / n);
                int add = k + r + 1, drop = k - r;
                if (add < length) { sum += from[Index(add)]; n++; }
                if (drop >= 0) { sum -= from[Index(drop)]; n--; }
            }
        }
    }

    /// <summary>Pulls the edge in by one pixel, which removes the coloured halo a removed background leaves.</summary>
    internal void Shrink()
    {
        Touch();
        var copy = (byte[])Mask.Clone();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            if (copy[i] == 0) continue;
            int low = copy[i];
            if (x > 0) low = Math.Min(low, copy[i - 1]);
            if (x < Width - 1) low = Math.Min(low, copy[i + 1]);
            if (y > 0) low = Math.Min(low, copy[i - Width]);
            if (y < Height - 1) low = Math.Min(low, copy[i + Width]);
            Mask[i] = (byte)low;
        }
    }

    // ───────────────────────────── Geometry ─────────────────────────────

    /// <summary>The smallest rectangle holding every pixel at least slightly kept.</summary>
    internal Int32Rect Bounds(byte threshold = 8)
    {
        int left = Width, top = Height, right = -1, bottom = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (Mask[y * Width + x] < threshold) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    internal bool Crop(Int32Rect rect)
    {
        int x0 = Math.Clamp(rect.X, 0, Width - 1), y0 = Math.Clamp(rect.Y, 0, Height - 1);
        int w = Math.Clamp(rect.Width, 1, Width - x0), h = Math.Clamp(rect.Height, 1, Height - y0);
        if (w == Width && h == Height) return false;
        Touch();
        var pixels = new byte[w * h * 4];
        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(Pixels, ((y0 + y) * Width + x0) * 4, pixels, y * w * 4, w * 4);
            Buffer.BlockCopy(Mask, (y0 + y) * Width + x0, mask, y * w, w);
        }
        (Width, Height, Pixels, Mask) = (w, h, pixels, mask);
        return true;
    }

    /// <summary>Crops to what is kept plus a small margin.</summary>
    internal bool Trim(int margin = 2)
    {
        var b = Bounds();
        if (b.IsEmpty) return false;
        int x = Math.Max(0, b.X - margin), y = Math.Max(0, b.Y - margin);
        return Crop(new Int32Rect(x, y, Math.Min(Width - x, b.Width + 2 * margin), Math.Min(Height - y, b.Height + 2 * margin)));
    }

    internal void RotateRight()
    {
        Touch();
        int w = Height, h = Width;
        var pixels = new byte[Pixels.Length];
        var mask = new byte[Mask.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int nx = Height - 1 - y, ny = x;
            int from = y * Width + x, to = ny * w + nx;
            mask[to] = Mask[from];
            Buffer.BlockCopy(Pixels, from * 4, pixels, to * 4, 4);
        }
        (Width, Height, Pixels, Mask) = (w, h, pixels, mask);
    }

    internal void FlipHorizontal()
    {
        Touch();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width / 2; x++)
        {
            int a = y * Width + x, b = y * Width + Width - 1 - x;
            (Mask[a], Mask[b]) = (Mask[b], Mask[a]);
            for (int c = 0; c < 4; c++) (Pixels[a * 4 + c], Pixels[b * 4 + c]) = (Pixels[b * 4 + c], Pixels[a * 4 + c]);
        }
    }

    /// <summary>Replaces the mask, e.g. with one the background model made. Values are combined with the current mask.</summary>
    internal void IntersectMask(byte[] mask)
    {
        if (mask.Length != Mask.Length) throw new ArgumentException("Maske boyutu uyuşmuyor.");
        Touch();
        for (int i = 0; i < Mask.Length; i++) Mask[i] = (byte)(Mask[i] * mask[i] / 255);
    }
}
