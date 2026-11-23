using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace Petglare;

/// <summary>
/// Compares background-model methods on pictures with a known answer: the transparent mascot
/// composited onto local photos (Windows wallpapers), each turned 0°, 90°, 180° and 270°.
/// Run: dotnet run --project tests\Petglare.Tests -- --bench &lt;model&gt; &lt;mascot png&gt; &lt;report folder&gt;
/// </summary>
internal static class Bench
{
    private sealed record Method(string Name, Func<PetImage, float[]> Run);

    private sealed class Score
    {
        public readonly List<double> IoU = new(), Recall = new(), FalseBackground = new(), Consistency = new(), Millis = new();
        public readonly double[] IoUByTurn = new double[4];
        public readonly int[] CountByTurn = new int[4];
    }

    /// <param name="modelFolder">Folder holding the model files (u2netp.onnx, silueta.onnx, isnet-general-use.onnx); missing ones are skipped.</param>
    internal static int Run(string modelFolder, string mascotPath, string outDir)
    {
        BackgroundRemover.ModelFolder = modelFolder;
        Directory.CreateDirectory(outDir);
        var mascot = new FormatConvertedBitmap(PetImage.Decode(mascotPath, 1024), System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var backgrounds = Directory.EnumerateFiles(@"C:\Windows\Web", "*.jpg", SearchOption.AllDirectories)
            .Where(p => !p.Contains("touchkeyboard", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Take(8).ToList();
        var shapes = new[] { (900, 600), (600, 900), (720, 720) };

        var methods = new List<Method>();
        var stretch = BackgroundRemover.Fit.Stretch;
        var mean = BackgroundRemover.Merge.Mean;
        foreach (var model in new[] { BackgroundRemover.Small, BackgroundRemover.Silueta, BackgroundRemover.IsNet })
        {
            if (BackgroundRemover.Status(model) != BackgroundRemover.ModelStatus.Ready)
            {
                Console.WriteLine($"Skipped {model.Id}: not present or corrupt");
                continue;
            }
            methods.Add(new($"{model.Summary} · tek yön", i => BackgroundRemover.Probability(i.Pixels, i.Width, i.Height, stretch, default, model)));
            if (model != BackgroundRemover.IsNet)
                methods.Add(new($"{model.Summary} · 4 yön", i => BackgroundRemover.Combine(i, stretch, mean, default, model)));
        }
        if (methods.Count == 0) return 1;
        var scores = methods.ToDictionary(m => m.Name, _ => new Score());
        foreach (var method in methods) method.Run(new PetImage(4, 4, new byte[64], new byte[16])); // warm up

        int samples = 0;
        foreach (var bgPath in backgrounds)
        foreach (var (w, h) in shapes)
        foreach (bool offset in new[] { false, true })
        {
            var (pixels, truth) = Composite(bgPath, mascot, w, h, offset);
            samples++;
            foreach (var method in methods)
            {
                var score = scores[method.Name];
                var backMasks = new List<bool[]>();
                for (int turns = 0; turns < 4; turns++)
                {
                    var (rp, rw, rh) = BackgroundRemover.Rotate(pixels, w, h, 4, turns);
                    var image = new PetImage(rw, rh, rp, Enumerable.Repeat((byte)255, rw * rh).ToArray());
                    var clock = Stopwatch.StartNew();
                    var mask = BackgroundRemover.ToMask(method.Run(image));
                    score.Millis.Add(clock.Elapsed.TotalMilliseconds);
                    var back = BackgroundRemover.Rotate(mask, rw, rh, 1, (4 - turns) % 4).Data.Select(m => m >= 128).ToArray();
                    backMasks.Add(back);
                    double iou = IoU(back, truth);
                    score.IoU.Add(iou);
                    score.IoUByTurn[turns] += iou;
                    score.CountByTurn[turns]++;
                    int fg = truth.Count(t => t), bg = truth.Length - fg;
                    score.Recall.Add((double)Enumerable.Range(0, truth.Length).Count(k => truth[k] && back[k]) / fg);
                    score.FalseBackground.Add((double)Enumerable.Range(0, truth.Length).Count(k => !truth[k] && back[k]) / bg);
                }
                var pairs = new List<double>();
                for (int a = 0; a < 4; a++) for (int b = a + 1; b < 4; b++) pairs.Add(IoU(backMasks[a], backMasks[b]));
                score.Consistency.Add(pairs.Average());
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"Örnek: {samples} birleşik görsel × 4 yönelim ({backgrounds.Count} Windows duvar kâğıdı, 3 en-boy, 2 konum)");
        report.AppendLine("| Yöntem | IoU ort. | IoU en düşük | IoU 0° / 90° / 180° / 270° | Ön plan korunan | Yanlış ön plan | Yönelim tutarlılığı | Süre (ms) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var method in methods)
        {
            var s = scores[method.Name];
            var byTurn = string.Join(" / ", Enumerable.Range(0, 4).Select(t => $"{s.IoUByTurn[t] / s.CountByTurn[t]:0.000}"));
            report.AppendLine($"| {method.Name} | {s.IoU.Average():0.000} | {s.IoU.Min():0.000} | {byTurn} | %{s.Recall.Average() * 100:0.0} | "
                              + $"%{s.FalseBackground.Average() * 100:0.00} | {s.Consistency.Average():0.000} | {s.Millis.Average():0} |");
        }
        Console.Write(report);
        File.WriteAllText(Path.Combine(outDir, "bench.md"), report.ToString());
        return 0;
    }

    private static double IoU(bool[] a, bool[] b)
    {
        int inter = 0, union = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] && b[i]) inter++;
            if (a[i] || b[i]) union++;
        }
        return union == 0 ? 1 : (double)inter / union;
    }

    /// <summary>The mascot over a crop of the photo; the answer is where the mascot is at least half opaque.</summary>
    private static (byte[] Pixels, bool[] Truth) Composite(string bgPath, BitmapSource mascot, int w, int h, bool offset)
    {
        var bg = PetImage.FromBitmap(PetImage.Decode(bgPath, 1600), 1600);
        // Cover crop of the photo to w×h.
        double k = Math.Max((double)w / bg.Width, (double)h / bg.Height);
        int sx = (int)((bg.Width * k - w) / 2), sy = (int)((bg.Height * k - h) / 2);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int bx = Math.Min(bg.Width - 1, (int)((x + sx) / k)), by = Math.Min(bg.Height - 1, (int)((y + sy) / k));
            Array.Copy(bg.Pixels, (by * bg.Width + bx) * 4, pixels, (y * w + x) * 4, 4);
        }
        int size = (int)(Math.Min(w, h) * 0.55);
        var fg = new byte[mascot.PixelWidth * mascot.PixelHeight * 4];
        mascot.CopyPixels(fg, mascot.PixelWidth * 4, 0);
        int left = offset ? w / 8 : (w - size) / 2, top = offset ? h - size - h / 10 : (h - size) / 2;
        var truth = new bool[w * h];
        double f = (double)mascot.PixelWidth / size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int mx = (int)(x * f), my = (int)(y * f);
            int m = (my * mascot.PixelWidth + mx) * 4;
            int alpha = fg[m + 3];
            int p = ((top + y) * w + left + x) * 4;
            for (int c = 0; c < 3; c++) pixels[p + c] = (byte)((fg[m + c] * alpha + pixels[p + c] * (255 - alpha)) / 255);
            truth[(top + y) * w + left + x] = alpha >= 128;
        }
        return (pixels, truth);
    }
}
