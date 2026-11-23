using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Petglare;

/// <summary>A background model Petglare can download: where from, how to check it, how to feed it.</summary>
internal sealed record BackgroundModel(
    string Id, string Title, string Summary, string File, string Url, string Sha256, long Bytes,
    int Side, float[] Mean, float[] Deviation)
{
    internal const string Source = "github.com/danielgatis/rembg";
    internal string Megabytes => $"{Bytes / 1024.0 / 1024.0:0} MB";
}

/// <summary>
/// Removes photo backgrounds with salient-object models from the rembg project (Apache-2.0).
/// Models are not shipped: each is downloaded once, with the user's consent, and checked
/// against a fixed SHA-256 before first use. Everything runs locally on the CPU.
/// </summary>
internal static class BackgroundRemover
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetDeviation = [0.229f, 0.224f, 0.225f];

    /// <summary>U²-Net small: tiny and fast; the default.</summary>
    internal static readonly BackgroundModel Small = new("u2netp", "Hızlı", "U²-Net küçük · en hızlı",
        "u2netp.onnx", "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8", 4_574_861, 320, ImageNetMean, ImageNetDeviation);

    /// <summary>Silueta: U²-Net full, pruned to 43 MB.</summary>
    internal static readonly BackgroundModel Silueta = new("silueta", "Dengeli", "Silueta (U²-Net budanmış)",
        "silueta.onnx", "https://github.com/danielgatis/rembg/releases/download/v0.0.0/silueta.onnx",
        "75da6c8d2f8096ec743d071951be73b4a8bc7b3e51d9a6625d63644f90ffeedb", 44_173_029, 320, ImageNetMean, ImageNetDeviation);

    /// <summary>IS-Net general use: 1024² input, heavier, sharper outlines.</summary>
    internal static readonly BackgroundModel IsNet = new("isnet-general-use", "Kaliteli", "IS-Net genel · en ayrıntılı",
        "isnet-general-use.onnx", "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a", 178_648_008, 1024,
        [0.5f, 0.5f, 0.5f], [1f, 1f, 1f]);

    /// <summary>
    /// The models offered in Pet Workshop, in order. Silueta is defined for measurements but not
    /// offered: it dropped more of the subject than the small model (README, "Arka plan modeli").
    /// </summary>
    internal static IReadOnlyList<BackgroundModel> Models { get; set; } = [Small, IsNet];

    // Kept for the default model.
    internal const string ModelSource = BackgroundModel.Source;
    internal static long ModelBytes => Small.Bytes;

    private static readonly object gate = new();
    private static readonly Dictionary<string, InferenceSession> sessions = new();
    private static readonly Dictionary<string, string> paths = new();
    private static readonly Dictionary<string, (string Path, long Length, DateTime Written, bool Valid)> verified = new();

    /// <summary>Where models are kept; tests may point elsewhere.</summary>
    internal static string ModelFolder { get; set; } = Path.Combine(Preferences.Folder, "Models");

    internal static string PathOf(BackgroundModel model)
    {
        lock (gate) return paths.TryGetValue(model.Id, out var path) ? path : Path.Combine(ModelFolder, model.File);
    }

    /// <summary>Points one model at a specific file (tests and benchmarks).</summary>
    internal static void SetPath(BackgroundModel model, string path)
    {
        lock (gate)
        {
            paths[model.Id] = path;
            Forget(model);
        }
    }

    /// <summary>The default model's file.</summary>
    internal static string ModelPath
    {
        get => PathOf(Small);
        set => SetPath(Small, value);
    }

    internal enum ModelStatus { Missing, Corrupt, Ready }

    /// <summary>Missing, corrupt (wrong size or checksum) or ready. The checksum is cached until the file changes.</summary>
    internal static ModelStatus Status(BackgroundModel? model = null)
    {
        model ??= Small;
        var path = PathOf(model);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return ModelStatus.Missing;
            if (file.Length != model.Bytes) return ModelStatus.Corrupt;
            lock (gate)
            {
                if (!verified.TryGetValue(model.Id, out var v) || (v.Path, v.Length, v.Written) != (path, file.Length, file.LastWriteTimeUtc))
                    verified[model.Id] = v = (path, file.Length, file.LastWriteTimeUtc, HashMatches(path, model));
                return v.Valid ? ModelStatus.Ready : ModelStatus.Corrupt;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelStatus.Corrupt;
        }
    }

    private static void Forget(BackgroundModel model)
    {
        lock (gate)
        {
            if (sessions.Remove(model.Id, out var session)) session.Dispose();
            verified.Remove(model.Id);
        }
    }

    /// <summary>Removes a model file (bad, or no longer wanted) so it can be downloaded again.</summary>
    internal static void DeleteModel(BackgroundModel? model = null)
    {
        model ??= Small;
        Forget(model);
        try { File.Delete(PathOf(model)); }
        catch (IOException) { }
    }

    /// <summary>Downloads a model, refusing anything that is not exactly the expected file.</summary>
    internal static Task DownloadAsync(IProgress<double>? progress, CancellationToken cancel) => DownloadAsync(Small, progress, cancel);

    internal static async Task DownloadAsync(BackgroundModel model, IProgress<double>? progress, CancellationToken cancel)
    {
        var target = PathOf(model);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Petglare");
            using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > model.Bytes) throw new InvalidDataException("İndirilen model beklenenden büyük.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress?.Report((double)total / model.Bytes);
                }
            }
            if (!HashMatches(partial, model)) throw new InvalidDataException("İndirilen modelin özeti tutmadı; dosya silindi.");
            Forget(model);
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    private static bool HashMatches(string path, BackgroundModel model)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == model.Sha256;
    }

    private static InferenceSession Session(BackgroundModel model)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(model.Id, out var existing)) return existing;
        }
        if (Status(model) != ModelStatus.Ready) throw new InvalidDataException("Arka plan modeli eksik veya bozuk.");
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        var session = new InferenceSession(PathOf(model), options);
        lock (gate)
        {
            if (sessions.TryGetValue(model.Id, out var raced)) { session.Dispose(); return raced; }
            sessions[model.Id] = session;
            return session;
        }
    }

    /// <summary>How the picture is fitted into the model's square input.</summary>
    internal enum Fit
    {
        /// <summary>Squashed to a square, as the reference pipeline does.</summary>
        Stretch,
        /// <summary>Scaled to fit with its proportions kept; the rest is the neutral mean colour.</summary>
        Letterbox
    }

    /// <summary>How the four orientation results are combined in high quality mode.</summary>
    internal enum Merge { Mean, Max }

    /// <summary>The settings used by the app. Chosen from measurements (see README, "Arka plan modeli").</summary>
    internal static Fit FastFit { get; set; } = Fit.Stretch;
    internal static Fit QualityFit { get; set; } = Fit.Stretch;
    internal static Merge QualityMerge { get; set; } = Merge.Mean;

    /// <summary>
    /// A mask for <paramref name="image"/>: 255 where the subject is, 0 for background. High
    /// quality runs the model on the picture turned 0°, 90°, 180° and 270°, turns each result
    /// back and combines them (about four times slower). Safe off the UI thread; the image is only read.
    /// </summary>
    internal static byte[] Predict(PetImage image, CancellationToken cancel = default, bool highQuality = false,
        BackgroundModel? model = null)
    {
        model ??= Small;
        return highQuality
            ? ToMask(Combine(image, QualityFit, QualityMerge, cancel, model))
            : ToMask(Probability(image.Pixels, image.Width, image.Height, FastFit, cancel, model));
    }

    /// <summary>The four-orientation result before it becomes a mask (0–255 per pixel).</summary>
    internal static float[] Combine(PetImage image, Fit fit, Merge merge, CancellationToken cancel = default,
        BackgroundModel? model = null)
    {
        int w = image.Width, h = image.Height;
        var result = new float[w * h];
        for (int turns = 0; turns < 4; turns++)
        {
            cancel.ThrowIfCancellationRequested();
            var (pixels, rw, rh) = Rotate(image.Pixels, w, h, 4, turns);
            var turned = Probability(pixels, rw, rh, fit, cancel, model);
            var back = Rotate(turned, rw, rh, 1, (4 - turns) % 4).Data;
            for (int i = 0; i < result.Length; i++)
                result[i] = merge == Merge.Max ? Math.Max(result[i], back[i]) : result[i] + back[i] / 4f;
        }
        return result;
    }

    /// <summary>One run of the model: 0–255 per pixel at the picture's own size.</summary>
    internal static float[] Probability(byte[] bgra, int width, int height, Fit fit, CancellationToken cancel = default,
        BackgroundModel? model = null)
    {
        model ??= Small;
        var session = Session(model);
        int side = model.Side;
        cancel.ThrowIfCancellationRequested();

        // Where the picture sits inside the square input.
        int fw = side, fh = side;
        if (fit == Fit.Letterbox)
        {
            double k = (double)side / Math.Max(width, height);
            fw = Math.Clamp((int)Math.Round(width * k), 1, side);
            fh = Math.Clamp((int)Math.Round(height * k), 1, side);
        }
        int ox = (side - fw) / 2, oy = (side - fh) / 2;

        // Like the reference pipeline: divide by the brightest value, then the model's mean/std, RGB.
        // Letterbox padding is left at 0, which is the mean colour after normalisation.
        var input = new DenseTensor<float>(new[] { 1, 3, side, side });
        var small = Resample(bgra, width, height, 4, fw, fh);
        float brightest = 1;
        for (int i = 0; i < small.Length; i++) if ((i & 3) != 3) brightest = Math.Max(brightest, small[i]);
        var mean = model.Mean;
        var deviation = model.Deviation;
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            int p = (y * fw + x) * 4;
            float r = small[p + 2] / brightest, g = small[p + 1] / brightest, b = small[p] / brightest;
            input[0, 0, oy + y, ox + x] = (r - mean[0]) / deviation[0];
            input[0, 1, oy + y, ox + x] = (g - mean[1]) / deviation[1];
            input[0, 2, oy + y, ox + x] = (b - mean[2]) / deviation[2];
        }
        cancel.ThrowIfCancellationRequested();

        string name = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var output = results.First().AsTensor<float>();
        cancel.ThrowIfCancellationRequested();

        // Normalised over the picture area only (as the reference does over its whole input).
        var prediction = new float[fw * fh];
        float low = float.MaxValue, high = float.MinValue;
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            float v = output[0, 0, oy + y, ox + x];
            prediction[y * fw + x] = v;
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }
        float range = Math.Max(1e-6f, high - low);
        for (int i = 0; i < prediction.Length; i++) prediction[i] = (prediction[i] - low) / range * 255f;
        return Resample(prediction, fw, fh, 1, width, height);
    }

    /// <summary>Clean up the faint haze around the subject and settle the solid inside.</summary>
    internal static byte[] ToMask(float[] values)
    {
        var mask = new byte[values.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            float v = values[i];
            mask[i] = v < 20 ? (byte)0 : v > 235 ? (byte)255 : (byte)Math.Round((v - 20) / 215f * 255f);
        }
        return mask;
    }

    /// <summary>Turns a pixel grid clockwise by <paramref name="turns"/> quarter turns.</summary>
    internal static (T[] Data, int Width, int Height) Rotate<T>(T[] source, int width, int height, int channels, int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return ((T[])source.Clone(), width, height);
        int nw = turns == 2 ? width : height, nh = turns == 2 ? height : width;
        var result = new T[source.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var (nx, ny) = turns switch
            {
                1 => (height - 1 - y, x),
                2 => (width - 1 - x, height - 1 - y),
                _ => (y, width - 1 - x)
            };
            Array.Copy(source, (y * width + x) * channels, result, (ny * nw + nx) * channels, channels);
        }
        return (result, nw, nh);
    }

    private static float[] Resample(byte[] source, int width, int height, int channels, int toWidth, int toHeight)
    {
        var floats = new float[source.Length];
        for (int i = 0; i < source.Length; i++) floats[i] = source[i];
        return Resample(floats, width, height, channels, toWidth, toHeight);
    }

    /// <summary>Bilinear resampling; good enough both ways for a 320² mask.</summary>
    private static float[] Resample(float[] source, int width, int height, int channels, int toWidth, int toHeight)
    {
        var result = new float[toWidth * toHeight * channels];
        double sx = (double)width / toWidth, sy = (double)height / toHeight;
        for (int y = 0; y < toHeight; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, height - 1);
            int y0 = (int)fy, y1 = Math.Min(height - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < toWidth; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, width - 1);
                int x0 = (int)fx, x1 = Math.Min(width - 1, x0 + 1);
                double tx = fx - x0;
                for (int c = 0; c < channels; c++)
                {
                    double a = source[(y0 * width + x0) * channels + c], b = source[(y0 * width + x1) * channels + c];
                    double d = source[(y1 * width + x0) * channels + c], e = source[(y1 * width + x1) * channels + c];
                    result[(y * toWidth + x) * channels + c] =
                        (float)((a * (1 - tx) + b * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty);
                }
            }
        }
        return result;
    }
}
