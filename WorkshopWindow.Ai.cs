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

    partial void AddAutoTools()
    {
        aiButton = SmallButton(AiLabel(), async (_, _) => await AiClick());
        aiButton.ToolTip = "Fotoğraflarda konuyu bulur ve arka planı siler. Bilgisayarında çalışır; görsel hiçbir yere gönderilmez.";
        needsPicture.Add(aiButton);
        Tools.Children.Add(aiButton);
    }

    private static string AiLabel() => BackgroundRemover.ModelPresent
        ? "Yapay zekâ ile sil (fotoğraf)"
        : $"Yapay zekâ modelini indir ({BackgroundRemover.ModelBytes / 1024.0 / 1024.0:0.0} MB)";

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
            if (!BackgroundRemover.ModelPresent)
            {
                if (MessageBox.Show(this,
                        $"Fotoğraf arka planını silmek için U²-Net modeli bir kez indirilecek.\n\n"
                        + $"Dosya: u2netp.onnx ({BackgroundRemover.ModelBytes / 1024.0 / 1024.0:0.0} MB)\n"
                        + $"Kaynak: {BackgroundRemover.ModelSource} (Apache-2.0)\n\n"
                        + "Model bilgisayarında çalışır; görsellerin hiçbir yere gönderilmez. İndirilsin mi?",
                        "Pet Workshop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                aiButton.Content = "İndirmeyi durdur";
                var progress = new Progress<double>(p => Status.Text = $"Model indiriliyor… %{p * 100:0}");
                await BackgroundRemover.DownloadAsync(progress, aiCancel.Token);
                ShowStatus("Model indirildi");
            }

            var copy = image.Save();
            var working = new PetImage(copy.Width, copy.Height, copy.Pixels, copy.Mask);
            aiButton.Content = "Durdur";
            Status.Text = "Arka plan aranıyor…";
            var token = aiCancel.Token;
            var mask = await Task.Run(() => BackgroundRemover.Predict(working, token), token);
            if (image.Width != copy.Width || image.Height != copy.Height)
            {
                ShowStatus("Görsel bu sırada değişti; tekrar dene");
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
