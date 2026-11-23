using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Petglare;

/// <summary>Pet Workshop's photo background removal (see <see cref="BackgroundRemover"/>).</summary>
public partial class WorkshopWindow
{
    private Button? aiButton;
    private CancellationTokenSource? aiCancel;
    private bool highQuality;
    private BackgroundModel aiModel = BackgroundRemover.Small;

    partial void AddAutoTools()
    {
        if (BackgroundRemover.Models.Count > 1)
        {
            // One choice per model; each is downloaded only when first used.
            var choices = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            string group = NewGroup();
            foreach (var model in BackgroundRemover.Models)
            {
                var option = new RadioButton
                {
                    Content = model.Title, GroupName = group, IsChecked = model == aiModel, Style = StyleOf("Segment"),
                    Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Margin = new Thickness(0, 0, 4, 4),
                    ToolTip = $"{model.Summary} · {model.Megabytes}"
                };
                option.Checked += (_, _) =>
                {
                    aiModel = model;
                    if (aiButton is not null && aiCancel is null) aiButton.Content = AiLabel();
                };
                choices.Children.Add(option);
            }
            Tools.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Canvas"), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(3, 3, 0, 0), Margin = new Thickness(0, 4, 0, 6), Child = choices,
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }
        aiButton = SmallButton(AiLabel(), async (_, _) => await AiClick());
        aiButton.ToolTip = "Fotoğraflarda konuyu bulur ve arka planı siler. Bilgisayarında çalışır; görsel hiçbir yere gönderilmez.";
        needsPicture.Add(aiButton);
        Tools.Children.Add(aiButton);
        var quality = new CheckBox
        {
            Content = "Yüksek kalite (4 yönde dener, ~4 kat yavaş)", IsChecked = highQuality,
            Foreground = (System.Windows.Media.Brush)FindResource("Soft"), Margin = new Thickness(0, 2, 0, 0),
            ToolTip = "Fotoğrafı 0°, 90°, 180° ve 270° döndürüp ayrı ayrı çalıştırır, sonuçları birleştirir. "
                      + "Yan çekilmiş fotoğraflarda daha tutarlı olabilir."
        };
        quality.Click += (_, _) => highQuality = quality.IsChecked == true;
        Tools.Children.Add(quality);
    }

    private string AiLabel() => BackgroundRemover.Status(aiModel) switch
    {
        BackgroundRemover.ModelStatus.Ready => "Yapay zekâ ile sil (fotoğraf)",
        BackgroundRemover.ModelStatus.Corrupt => $"Bozuk modeli onar ({aiModel.Megabytes})",
        _ => $"Yapay zekâ modelini indir ({aiModel.Megabytes})"
    };

    /// <summary>
    /// A mask may be applied only to the very picture, in the very state, it was computed for:
    /// same object (no new picture opened) and same revision (no edit, flip, rotation or undo since).
    /// </summary>
    internal static bool MaskStillFits(PetImage? current, PetImage source, long revision) =>
        ReferenceEquals(current, source) && current.Revision == revision;

    private async Task AiClick()
    {
        if (image is null || aiButton is null) return;
        if (aiCancel is not null)
        {
            aiCancel.Cancel();
            return;
        }
        aiCancel = new CancellationTokenSource();
        try
        {
            var model = aiModel;
            var status = BackgroundRemover.Status(model);
            if (status != BackgroundRemover.ModelStatus.Ready)
            {
                var why = status == BackgroundRemover.ModelStatus.Corrupt
                    ? "Kayıtlı model dosyası bozuk veya eksik (özeti tutmuyor). Silinip yeniden indirilecek.\n\n"
                    : $"Fotoğraf arka planını silmek için {model.Summary} modeli bir kez indirilecek.\n\n";
                if (!Confirm(why
                        + $"Dosya: {model.File} ({model.Megabytes})\n"
                        + $"Kaynak: {BackgroundModel.Source} (Apache-2.0)\n\n"
                        + "Model bilgisayarında çalışır; görsellerin hiçbir yere gönderilmez. İndirilsin mi?"))
                    return;
                BackgroundRemover.DeleteModel(model);
                aiButton.Content = "İndirmeyi durdur";
                var progress = new Progress<double>(p => Status.Text = $"Model indiriliyor… %{p * 100:0}");
                await BackgroundRemover.DownloadAsync(model, progress, aiCancel.Token);
                ShowStatus("Model indirildi");
            }

            var source = image;
            long revision = image.Revision;
            var working = image.Copy();
            aiButton.Content = "Durdur";
            Status.Text = highQuality ? "Arka plan aranıyor (yüksek kalite, 4 yön)…" : "Arka plan aranıyor…";
            var token = aiCancel.Token;
            bool quality = highQuality;
            var mask = await Task.Run(() => BackgroundRemover.Predict(working, token, quality, model), token);
            if (!MaskStillFits(image, source, revision))
            {
                ShowStatus("Görsel bu sırada değişti; maske uygulanmadı, tekrar dene");
                return;
            }
            Apply(i => { i.IntersectMask(mask); return true; }, "Arka plan silindi; kalan kenarları fırçayla düzeltebilirsin");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Durduruldu");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            ShowStatus("Yapılamadı: " + ex.Message);
        }
        finally
        {
            aiCancel?.Dispose();
            aiCancel = null;
            aiButton.Content = AiLabel();
        }
    }
}
