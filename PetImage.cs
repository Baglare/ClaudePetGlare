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
    /// <summary>One byte per pixel: 1 where the user marked "keep"; colour tools and the background model leave it alone.</summary>
    internal byte[] Protect { get; private set; }

    internal PetImage(int width, int height, byte[] pixels, byte[] mask, byte[]? protect = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height * 4 || mask.Length != width * height
            || (protect is not null && protect.Length != width * height))
            throw new ArgumentException("Bozuk görsel verisi.");
        Width = width;
        Height = height;
        Pixels = pixels;
        Mask = mask;
        Protect = protect ?? new byte[width * height];
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

    internal sealed record Snapshot(int Width, int Height, byte[] Pixels, byte[] Mask, byte[] Protect);

    internal Snapshot Save() => new(Width, Height, (byte[])Pixels.Clone(), (byte[])Mask.Clone(), (byte[])Protect.Clone());

    /// <summary>An independent copy (for work off the UI thread).</summary>
    internal PetImage Copy() => new(Width, Height, (byte[])Pixels.Clone(), (byte[])Mask.Clone(), (byte[])Protect.Clone());

    internal void Restore(Snapshot snapshot)
    {
        Touch();
        Width = snapshot.Width;
        Height = snapshot.Height;
        Pixels = (byte[])snapshot.Pixels.Clone();
        Mask = (byte[])snapshot.Mask.Clone();
        Protect = (byte[])snapshot.Protect.Clone();
        lab = null;
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

    // ───────────────────────────── Protection ─────────────────────────────

    /// <summary>Marks (or unmarks) a round area the colour tools and the background model must keep.</summary>
    internal bool ProtectDab(double cx, double cy, double radius, bool add)
    {
        Touch();
        radius = Math.Max(0.5, radius);
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius));
        bool changed = false;
        byte value = add ? (byte)1 : (byte)0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) > radius * radius) continue;
            int i = y * Width + x;
            if (Protect[i] != value) { Protect[i] = value; changed = true; }
        }
        return changed;
    }

    internal bool ProtectStroke(Point from, Point to, double radius, bool add)
    {
        double length = (to - from).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, radius / 3)));
        bool changed = false;
        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            changed |= ProtectDab(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, radius, add);
        }
        return changed;
    }

    internal bool HasProtection => Array.IndexOf(Protect, (byte)1) >= 0;

    // ───────────────────────────── Colour tools ─────────────────────────────

    /// <summary>A colour in CIELAB (D65), where distance follows what the eye sees.</summary>
    internal readonly record struct LabColor(float L, float A, float B)
    {
        /// <summary>CIE76 ΔE: about 2 is barely visible, 10 clearly different.</summary>
        internal float DistanceTo(LabColor o) =>
            MathF.Sqrt((L - o.L) * (L - o.L) + (A - o.A) * (A - o.A) + (B - o.B) * (B - o.B));
    }

    /// <summary>Default and range of the colour tolerance, in ΔE.</summary>
    internal const int DefaultTolerance = 6, MaxTolerance = 40;

    private float[]? lab;

    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(v =>
    {
        double c = v / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }).ToArray();

    internal static LabColor ToLab(byte r, byte g, byte b)
    {
        float lr = Linear[r], lg = Linear[g], lb = Linear[b];
        float x = (0.4124f * lr + 0.3576f * lg + 0.1805f * lb) / 0.95047f;
        float y = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
        float z = (0.0193f * lr + 0.1192f * lg + 0.9505f * lb) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        return new LabColor(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>Lab of every pixel, computed once per picture state.</summary>
    private float[] LabPixels()
    {
        if (lab is { } cached && cached.Length == Width * Height * 3) return cached;
        var result = new float[Width * Height * 3];
        for (int i = 0, p = 0; i < Width * Height; i++, p += 4)
        {
            var c = ToLab(Pixels[p + 2], Pixels[p + 1], Pixels[p]);
            result[i * 3] = c.L; result[i * 3 + 1] = c.A; result[i * 3 + 2] = c.B;
        }
        return lab = result;
    }

    internal LabColor LabAt(int i)
    {
        var l = LabPixels();
        return new LabColor(l[i * 3], l[i * 3 + 1], l[i * 3 + 2]);
    }

    /// <summary>The average colour of a 3×3 patch, for picking a background colour.</summary>
    internal LabColor SampleColor(int x, int y)
    {
        float sl = 0, sa = 0, sb = 0;
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int px = Math.Clamp(x + dx, 0, Width - 1), py = Math.Clamp(y + dy, 0, Height - 1);
            var c = LabAt(py * Width + px);
            sl += c.L; sa += c.A; sb += c.B; n++;
        }
        return new LabColor(sl / n, sa / n, sb / n);
    }

    internal (byte R, byte G, byte B) RgbAt(int x, int y)
    {
        int p = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 4;
        return (Pixels[p + 2], Pixels[p + 1], Pixels[p]);
    }

    private bool Near(int i, IReadOnlyList<LabColor> colors, float limit)
    {
        var l = LabPixels();
        var c = new LabColor(l[i * 3], l[i * 3 + 1], l[i * 3 + 2]);
        for (int k = 0; k < colors.Count; k++) if (c.DistanceTo(colors[k]) <= limit) return true;
        return false;
    }

    /// <summary>
    /// Which pixels the magic wand would erase for a click at (x, y), without changing anything.
    /// Tolerance is a ΔE; protected pixels are never included.
    /// </summary>
    internal bool[] WandRemoval(int x, int y, double tolerance, bool contiguous)
    {
        var remove = new bool[Width * Height];
        if (x < 0 || y < 0 || x >= Width || y >= Height) return remove;
        var colors = new[] { LabAt(y * Width + x) };
        float limit = (float)Math.Clamp(tolerance, 0, 100);
        if (!contiguous)
        {
            for (int i = 0; i < remove.Length; i++)
                remove[i] = Mask[i] != 0 && Protect[i] == 0 && Near(i, colors, limit);
            return remove;
        }
        Flood(new[] { y * Width + x }, i => Near(i, colors, limit), remove);
        return remove;
    }

    /// <summary>
    /// Which pixels the background remover would erase, without changing anything: from the
    /// picture's edges inward while the colour stays within <paramref name="tolerance"/> (ΔE) of
    /// one of <paramref name="colors"/> (the edge's main colours when none are picked).
    /// </summary>
    internal bool[] EdgeRemoval(double tolerance, IReadOnlyList<LabColor>? colors = null)
    {
        var remove = new bool[Width * Height];
        var edge = EdgeIndexes().ToArray();
        var targets = colors is { Count: > 0 } ? colors : EdgeColors();
        if (targets.Count == 0) return remove;
        float limit = (float)Math.Clamp(tolerance, 0, 100);
        Flood(edge, i => Near(i, targets, limit), remove);
        return remove;
    }

    /// <summary>Erases the given pixels (one undoable step). Returns how many were kept before.</summary>
    internal int Erase(bool[] remove)
    {
        if (remove.Length != Mask.Length) throw new ArgumentException("Seçim boyutu uyuşmuyor.");
        Touch();
        int count = 0;
        for (int i = 0; i < Mask.Length; i++)
            if (remove[i] && Mask[i] != 0 && Protect[i] == 0) { Mask[i] = 0; count++; }
        return count;
    }

    internal int FloodErase(int x, int y, double tolerance, bool contiguous) => Erase(WandRemoval(x, y, tolerance, contiguous));

    internal int EdgeErase(double tolerance, IReadOnlyList<LabColor>? colors = null) => Erase(EdgeRemoval(tolerance, colors));

    /// <summary>Pixels at least half kept.</summary>
    internal int KeptCount() => Mask.Count(m => m >= 128);

    private IEnumerable<int> EdgeIndexes()
    {
        for (int x = 0; x < Width; x++) { yield return x; if (Height > 1) yield return (Height - 1) * Width + x; }
        for (int y = 1; y < Height - 1; y++) { yield return y * Width; if (Width > 1) yield return y * Width + Width - 1; }
    }

    /// <summary>The colours that make up most of the edge (coarse buckets, each at least 10% of it), in Lab.</summary>
    internal List<LabColor> EdgeColors()
    {
        var buckets = new Dictionary<int, (long B, long G, long R, int N)>();
        foreach (int i in EdgeIndexes())
        {
            if (Mask[i] == 0) continue;
            int p = i * 4;
            int key = (Pixels[p] >> 5) << 6 | (Pixels[p + 1] >> 5) << 3 | Pixels[p + 2] >> 5;
            buckets.TryGetValue(key, out var b);
            buckets[key] = (b.B + Pixels[p], b.G + Pixels[p + 1], b.R + Pixels[p + 2], b.N + 1);
        }
        int total = buckets.Values.Sum(b => b.N);
        return buckets.Values.Where(b => b.N * 10 >= total).OrderByDescending(b => b.N).Take(4)
            .Select(b => ToLab((byte)(b.R / b.N), (byte)(b.G / b.N), (byte)(b.B / b.N))).ToList();
    }

    /// <summary>
    /// Four-way flood from the seeds. Kept pixels that pass <paramref name="include"/> are marked;
    /// already erased pixels are passed through but not marked; protected pixels stop the flood.
    /// </summary>
    private void Flood(IEnumerable<int> seeds, Func<int, bool> include, bool[] remove)
    {
        var seen = new bool[Mask.Length];
        var queue = new Queue<int>();
        foreach (int s in seeds)
            if (!seen[s]) { seen[s] = true; queue.Enqueue(s); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (Protect[i] != 0) continue;
            if (Mask[i] != 0)
            {
                if (!include(i)) continue;
                remove[i] = true;
            }
            int x = i % Width;
            if (x > 0 && !seen[i - 1]) { seen[i - 1] = true; queue.Enqueue(i - 1); }
            if (x < Width - 1 && !seen[i + 1]) { seen[i + 1] = true; queue.Enqueue(i + 1); }
            if (i >= Width && !seen[i - Width]) { seen[i - Width] = true; queue.Enqueue(i - Width); }
            if (i + Width < Mask.Length && !seen[i + Width]) { seen[i + Width] = true; queue.Enqueue(i + Width); }
        }
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
        var protect = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(Pixels, ((y0 + y) * Width + x0) * 4, pixels, y * w * 4, w * 4);
            Buffer.BlockCopy(Mask, (y0 + y) * Width + x0, mask, y * w, w);
            Buffer.BlockCopy(Protect, (y0 + y) * Width + x0, protect, y * w, w);
        }
        (Width, Height, Pixels, Mask, Protect) = (w, h, pixels, mask, protect);
        lab = null;
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
        var protect = new byte[Mask.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int nx = Height - 1 - y, ny = x;
            int from = y * Width + x, to = ny * w + nx;
            mask[to] = Mask[from];
            protect[to] = Protect[from];
            Buffer.BlockCopy(Pixels, from * 4, pixels, to * 4, 4);
        }
        (Width, Height, Pixels, Mask, Protect) = (w, h, pixels, mask, protect);
        lab = null;
    }

    internal void FlipHorizontal()
    {
        Touch();
        lab = null;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width / 2; x++)
        {
            int a = y * Width + x, b = y * Width + Width - 1 - x;
            (Mask[a], Mask[b]) = (Mask[b], Mask[a]);
            (Protect[a], Protect[b]) = (Protect[b], Protect[a]);
            for (int c = 0; c < 4; c++) (Pixels[a * 4 + c], Pixels[b * 4 + c]) = (Pixels[b * 4 + c], Pixels[a * 4 + c]);
        }
    }

    /// <summary>Replaces the mask, e.g. with one the background model made. Values are combined with the current mask.</summary>
    internal void IntersectMask(byte[] mask)
    {
        if (mask.Length != Mask.Length) throw new ArgumentException("Maske boyutu uyuşmuyor.");
        Touch();
        for (int i = 0; i < Mask.Length; i++)
            if (Protect[i] == 0) Mask[i] = (byte)(Mask[i] * mask[i] / 255);
    }
}
