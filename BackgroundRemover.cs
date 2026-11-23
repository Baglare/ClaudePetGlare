using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Petglare;

/// <summary>
/// Removes photo backgrounds with U²-Net (small variant, Apache-2.0). The model is not
/// shipped: it is downloaded once, with the user's consent, and checked against a fixed
/// SHA-256 before every first use. Everything runs locally on the CPU.
/// </summary>
internal static class BackgroundRemover
{
    internal const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    internal const string ModelSource = "github.com/danielgatis/rembg";
    internal const string ModelSha256 = "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8";
    internal const long ModelBytes = 4_574_861;
    private const int Side = 320;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];
    private static readonly object gate = new();
    private static InferenceSession? session;

    /// <summary>Where the model is kept; tests may point elsewhere.</summary>
    internal static string ModelPath { get; set; } = Path.Combine(Preferences.Folder, "Models", "u2netp.onnx");

    internal enum ModelStatus { Missing, Corrupt, Ready }

    private static (string Path, long Length, DateTime Written, bool Valid)? verified;

    /// <summary>Missing, corrupt (wrong size or checksum) or ready. The checksum is cached until the file changes.</summary>
    internal static ModelStatus Status()
    {
        try
        {
            var file = new FileInfo(ModelPath);
            if (!file.Exists) return ModelStatus.Missing;
            if (file.Length != ModelBytes) return ModelStatus.Corrupt;
            var key = (ModelPath, file.Length, file.LastWriteTimeUtc);
            if (verified is not { } v || (v.Path, v.Length, v.Written) != key)
                verified = (key.ModelPath, key.Length, key.LastWriteTimeUtc, HashMatches(ModelPath));
            return verified.Value.Valid ? ModelStatus.Ready : ModelStatus.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelStatus.Corrupt;
        }
    }

    /// <summary>Removes a bad model so it can be downloaded again.</summary>
    internal static void DeleteModel()
    {
        lock (gate)
        {
            session?.Dispose();
            session = null;
        }
        verified = null;
        try { File.Delete(ModelPath); }
        catch (IOException) { }
    }

    internal static bool ModelPresent
    {
        get
        {
            try { return new FileInfo(ModelPath) is { Exists: true, Length: ModelBytes }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Downloads the model, refusing anything that is not exactly the expected file.</summary>
    internal static async Task DownloadAsync(IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Petglare");
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
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
                    if (total > ModelBytes) throw new InvalidDataException("İndirilen model beklenenden büyük.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress?.Report((double)total / ModelBytes);
                }
            }
            if (!HashMatches(partial)) throw new InvalidDataException("İndirilen modelin özeti tutmadı; dosya silindi.");
            File.Move(partial, ModelPath, overwrite: true);
            verified = null;
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    private static bool HashMatches(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == ModelSha256;
    }

    private static InferenceSession Session()
    {
        lock (gate)
        {
            if (session is not null) return session;
            if (Status() != ModelStatus.Ready)
                throw new InvalidDataException("Arka plan modeli eksik veya bozuk.");
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            session = new InferenceSession(ModelPath, options);
            return session;
        }
    }

    /// <summary>How the picture is fitted into the model's 320×320 input.</summary>
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
    internal static byte[] Predict(PetImage image, CancellationToken cancel = default, bool highQuality = false) =>
        highQuality
            ? ToMask(Combine(image, QualityFit, QualityMerge, cancel))
            : ToMask(Probability(image.Pixels, image.Width, image.Height, FastFit, cancel));

    /// <summary>The four-orientation result before it becomes a mask (0–255 per pixel).</summary>
    internal static float[] Combine(PetImage image, Fit fit, Merge merge, CancellationToken cancel = default)
    {
        int w = image.Width, h = image.Height;
        var result = new float[w * h];
        for (int turns = 0; turns < 4; turns++)
        {
            cancel.ThrowIfCancellationRequested();
            var (pixels, rw, rh) = Rotate(image.Pixels, w, h, 4, turns);
            var turned = Probability(pixels, rw, rh, fit, cancel);
            var back = Rotate(turned, rw, rh, 1, (4 - turns) % 4).Data;
            for (int i = 0; i < result.Length; i++)
                result[i] = merge == Merge.Max ? Math.Max(result[i], back[i]) : result[i] + back[i] / 4f;
        }
        return result;
    }

    /// <summary>One run of the model: 0–255 per pixel at the picture's own size.</summary>
    internal static float[] Probability(byte[] bgra, int width, int height, Fit fit, CancellationToken cancel = default)
    {
        var model = Session();
        cancel.ThrowIfCancellationRequested();

        // Where the picture sits inside the 320×320 input.
        int fw = Side, fh = Side;
        if (fit == Fit.Letterbox)
        {
            double k = (double)Side / Math.Max(width, height);
            fw = Math.Clamp((int)Math.Round(width * k), 1, Side);
            fh = Math.Clamp((int)Math.Round(height * k), 1, Side);
        }
        int ox = (Side - fw) / 2, oy = (Side - fh) / 2;

        // Like the reference pipeline: divide by the brightest value, then ImageNet mean/std, RGB.
        // Letterbox padding is left at 0, which is the mean colour after normalisation.
        var input = new DenseTensor<float>(new[] { 1, 3, Side, Side });
        var small = Resample(bgra, width, height, 4, fw, fh);
        float brightest = 1;
        for (int i = 0; i < small.Length; i++) if ((i & 3) != 3) brightest = Math.Max(brightest, small[i]);
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            int p = (y * fw + x) * 4;
            float r = small[p + 2] / brightest, g = small[p + 1] / brightest, b = small[p] / brightest;
            input[0, 0, oy + y, ox + x] = (r - Mean[0]) / Deviation[0];
            input[0, 1, oy + y, ox + x] = (g - Mean[1]) / Deviation[1];
            input[0, 2, oy + y, ox + x] = (b - Mean[2]) / Deviation[2];
        }
        cancel.ThrowIfCancellationRequested();

        string name = model.InputMetadata.Keys is { } keys ? System.Linq.Enumerable.First(keys) : "input.1";
        using var results = model.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var output = System.Linq.Enumerable.First(results).AsTensor<float>();
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
