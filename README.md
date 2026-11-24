# Petglare

![Petglare](Assets/Petglare-README-Banner-2400x750.png)

Petglare, Windows 11’de Claude Desktop’ın **Code → Local** oturumlarından gelen durumları gösteren küçük bir masaüstü maskotu.

## Kurulum

Windows 11 x64, Claude Desktop ve [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) gerekir.

1. GitHub Releases’tan `Petglare-Windows-x64.zip` dosyasını indir ve kalıcı bir klasöre çıkar.
2. `Kur.cmd` dosyasına çift tıkla. Kurulum Petglare hook’larını ekler ve maskotu açar.
3. Claude Desktop’ta **Code → Local** oturumu aç.

Sonraki açılışlarda `Petglare.exe` dosyasını çalıştır veya menüden **Windows açılırken başlat** seçeneğini aç. Eski bir sürümden geçiyorsan menüdeki **Bağlantı teşhisi → Hook’ları kur / onar** yeni olayları ekler. Eski bir Petglare açıksa menüden kapatıp yeni EXE’yi aç. Dosyaları taşıdıysan yeni konumdan `Kur.cmd` dosyasını tekrar çalıştır. Hook’ları kaldırmak için `Hooklari-Kaldir.cmd` dosyasına çift tıkla.

Kurulum, Claude’un kullanıcı ayarlarına yazar (`CLAUDE_CONFIG_DIR` tanımlıysa o klasöre, aksi halde `~/.claude/settings.json` dosyasına). Diğer ayarları ve hook’ları korur. Tekrar kurulum Petglare hook’larını çoğaltmaz. Komut satırından özel bir ayar dosyası için `Petglare.exe --install-hooks <dosya>` ve `Petglare.exe --uninstall-hooks <dosya>` seçeneklerini kullanabilirsin.

## Maskotu kullanma

- **Sol tık** Claude Desktop’ı açar. **Sürükleme** konumu değiştirir; ekran kenarına yakın bırakınca kenara yaslanır.
- Maskotun üzerine gelince beliren **− / +** düğmeleri boyutu %10’luk adımlarla %60 ile %200 arasında değiştirir.
- **Sağ tık** (maskotta veya bildirim alanındaki simgede) kısa bir menü açar: oturum listesi, **Ayarlar…**, **Pet Workshop…**, Claude Desktop’ı aç, maskotu gizle, bağlantı teşhisi ve kapat.
- **Ayarlar** penceresinde bütün kalıcı seçenekler tek yerde durur. Soldaki önizleme masaüstündeki maskotla aynı çizimi kullanır; çalışırken, onay, bitti, parıltı ve çoklu sohbet hallerine bakabilirsin. Değişiklikler **Uygula**’ya basınca masaüstündeki maskota geçer ve kaydedilir; **Kapat** uygulanmamış değişiklikleri bırakır.
- Bildirim alanındaki simgeye **sol tık** maskotu gizler veya geri getirir. Simgedeki renkli nokta genel durumu gösterir: mavi çalışıyor, sarı senden bir şey bekliyor, kırmızı hata, yeşil bitti.
- Petglare zaten açıkken `Petglare.exe` dosyasını tekrar çalıştırmak gizli maskotu geri getirir.

### Durumlar

Balon, `VoxForge · Program.cs düzenleniyor` gibi önce projeyi, sonra yapılan işi gösterir. Proje adı Claude’un çalışma klasöründen gelir; `.claude\worktrees\…` altındaki çalışma ağaçları ana deponun adıyla görünür.

| Durum | Ne zaman |
|---|---|
| Claude düşünüyor | İstek gönderildiğinde ve her araç bittikten sonra |
| Dosya okunuyor / düzenleniyor, Komut çalıştırılıyor, Dosyalar aranıyor, Web’e bakılıyor, Alt ajan çalışıyor | Araç başladığında |
| Komut başarısız oldu (sarı) | Araç hata döndürdüğünde, dört saniye |
| Onay bekleniyor, Sana soru soruyor, Plan onayı bekleniyor (sarı çerçeve) | Claude senden bir şey beklediğinde |
| ✓ | Ana yanıt bittiğinde, dört saniye; ardından maskotun kenarında küçük bir yıldız kalır |
| Claude API hatası | Yanıt API hatasıyla bittiğinde, beş saniye |

### Birden fazla oturum

Petglare bütün Claude oturumlarını ayrı ayrı izler ve her sohbet için ayrı bir balon gösterir; balonlar alt alta dizilir (Ayarlar’dan 1–3 arası). En önemli sohbet maskota en yakın durur: senden bir şey bekleyen sohbet hatadan, hata çalışan sohbetten, çalışan sohbet bitmiş olandan önce gelir; eşitlikte en son hareket eden kazanır. Balon boyutu %85 ile %130 arasında ayarlanır. Aynı anda iki veya daha fazla oturum çalışıyorsa maskotun köşesinde sayı görünür. Bütün oturumların listesi menünün en üstünde ve maskotun ipucunda yer alır.

Bir yanıt bitince kalan parıltı, Claude Desktop penceresine geçtiğinde veya maskota tıklayıp Claude’u açtığında kaybolur. Claude Desktop hangi sohbetin açık olduğunu dışarıya bildirmediği için Petglare, Claude penceresine bakmayı sohbetleri görmüş sayar.

### Bildirimler

Menüdeki **Bildirimler** bölümünden Windows bildirimini ve kısa sesi ayrı ayrı açıp kapatabilirsin. Bildirim; Claude onay veya yanıt beklediğinde, on saniyeden uzun süren bir yanıt bittiğinde ve hata olduğunda gelir. **Claude öndeyken sessiz kal** açıkken Claude Desktop penceresine bakıyorsan bildirim gönderilmez. Bildirime tıklamak Claude Desktop’ı açar.

### Görünüm ve ekranda kalma

- **Maskot**: üç şekil (Kedi, Damla, Hayalet) ve kendi pet’lerin, beş renk, **Maskot boyutu** (%60–200) ve **Saydamlık** (%30–100).
- **Balonlar**: dört stil, **Balon boyutu** (%85–130), aynı anda gösterilen sohbet sayısı, proje adı ve tamamlanma parıltısı.
- **Her zaman en üstte**: maskotu her saniye yeniden en üste koyar; kenarlıksız pencere modundaki oyunların üstünde de kalır. Özel (exclusive) tam ekran modundaki oyunların üstüne Windows hiçbir pencereyi çizdirmez; o oyunlarda görüntü ayarından kenarlıksız pencereyi seç.
- **Tam ekranda gizle** (varsayılan kapalı): açarsan aynı ekranda tam ekran bir uygulama açıkken maskot saklanır. Kapalıyken maskot kenarlıksız oyunun, videonun veya sunumun üstünde kalır. Daha önce kaydedilmiş seçim korunur.
- **Sol tık**: *Açık*, *Tam ekranda kapalı* veya *Kapalı*. Kapalıyken sol tık Claude’u açmaz, maskotu taşımaz ve − / + düğmeleri görünmez; sağ tık menüsü her zaman çalışır. Oyun oynarken yanlışlıkla tıklamaya karşı *Tam ekranda kapalı* seçeneği uygundur.
- **Ekran kenarlarına yasla**, **Windows açılırken başlat** (`HKCU\…\Run`; EXE’yi taşırsan yeniden aç) ve **Sağ alt köşeye taşı**. Bir monitör çıkarılırsa maskot görünen bir ekrana geçer, monitör geri gelince eski yerine döner.
- Maskot monitörün tamamında dolaşır: görev çubuğunun üstüne binebilir ve ekranın gerçek kenarlarına kadar gider. Kenara yaslanma ekran kenarına tam bitişik, görev çubuğu kenarına ise küçük bir boşlukla oturur; hangisi yakınsa onu seçer. Varsayılan konum ve **Sağ alt köşeye taşı** görev çubuğunun üstünde kalır.

Boyut ve saydamlık sürgüyle tam istediğin değere ayarlanır; altındaki hazır düğmeler (%80, Opak, Küçük…) sık kullanılan değerlere atlar. Sürgü odaktayken ok tuşları 1, Page Up / Page Down 10 adım değiştirir.

### Pet Workshop: kendi pet’in

**Ayarlar → Şekil** satırındaki **+** karosu veya menüdeki **Pet Workshop…** bir görseli maskota çevirir.

1. Görseli sürükle, **Ctrl+V** ile yapıştır veya **Görsel seç…** (PNG, JPG, BMP, GIF, WebP). En çok 64 MB ve 40 megapiksel; büyük görseller açılırken 1024 piksele küçültülür.
2. Arka planı sil:
   - **Önizle: kenardan sil**: düz renkli arka planlar için. Silinecek yerler pembe görünür; **Uygula** demeden hiçbir şey silinmez. **Renk toleransı** algısal renk farkıyla (CIELAB ΔE) ölçülür, varsayılanı %6'dır; sürgü değiştikçe önizleme güncellenir. Arka plan rengini **Renk seç** aracıyla kendin seçebilirsin (birden çok renk olur); seçmezsen kenarlardaki ana renkler kullanılır. Görünen kısmın %90'ından fazlası silinecekse uyarı çıkar ve uygulamadan önce sorulur.
   - **Koru** fırçası: renkten silme, sihirli değnek ve yapay zekânın dokunmayacağı yeri yeşille boyar (**Korumayı kaldır** ile silinir).
   - **Yapay zekâ ile sil**: fotoğraflar için iki model var: **Hızlı** (U²-Net küçük, `u2netp.onnx`, 4,4 MB) ve **Kaliteli** (IS-Net genel, `isnet-general-use.onnx`, 170 MB; daha az ön plan siler, yaklaşık 6 kat yavaş). İkisi de [rembg](https://github.com/danielgatis/rembg) projesinden, Apache-2.0; seçtiğin model ilk kullanımda senin onayınla bir kez indirilir ve sabit SHA-256 ile doğrulanır. Model bilgisayarında çalışır; görselin hiçbir yere gönderilmez. Model dosyası bozulursa düğme **Bozuk modeli onar** olur ve onayınla dosyayı silip yeniden indirir. **Yüksek kalite** seçeneği modeli fotoğrafın 0°, 90°, 180° ve 270° döndürülmüş hâllerinde çalıştırıp sonuçları birleştirir; yaklaşık dört kat yavaştır (ölçüm sonuçları aşağıda).
3. İnce ayar: **Silgi** ve **Geri getir** fırçaları (boyut, sertlik; `[` `]`), tıklanan renk bölgesini silen **Sihirli değnek**, **Kırp**, **Kenarı daralt** (renk saçağını alır), **Kenarı yumuşat**, **Boşlukları kırp**, döndür ve çevir.
4. Sağdaki ön izleme masaüstündeki görünümü gösterir. Ad verip **Kaydet**.

Araçlar yalnız görselin üzerindeki maskeyi değiştirir; silinen yer **Geri getir** ile boyanabilir, **Ctrl+Z / Ctrl+Y** son 20 adımı geri alır. Tekerlek yakınlaştırır, sağ tık veya **Boşluk** ile sürükleme kaydırır, **Ctrl+0** sığdırır. Kısayollar: **E** silgi, **R** geri getir, **W** değnek, **P** renk seç, **K** koru, **C** kırp, **Esc** önizlemeyi kapatır.

Pet’ler `%LOCALAPPDATA%\Petglare\Pets` altında saklanır. Ayarlar’da kendi pet’ine sağ tıklayınca **Düzenle**, **Dışa aktar** (paylaşılabilir `.petglare` dosyası) ve **Sil** çıkar; **Pet dosyası içe aktar…** başkasının `.petglare` dosyasını ekler (yalnız görsel alınır, boyutu denetlenir ve yeniden kodlanır). Kendi pet’lerinde renkler görselden gelir; durum balonları, rozet ve parıltı aynen çalışır.

### Online Workshop

Topluluk pet’leri herkese açık [Baglare/Petglare-Workshop](https://github.com/Baglare/Petglare-Workshop) deposunda durur.

- **Bulmak ve eklemek:** **Ayarlar → Workshop → Workshop’a bağlan (internet)** açıkken **Workshop’tan pet bul…** kataloğu gösterir; aranır ve **Ekle** ile pet kütüphanene girer. Petglare yalnız bu anahtar açıkken ve yalnız `raw.githubusercontent.com` üzerinden katalog dosyasını ve pet görsellerini okur; varsayılan kapalıdır. Her görsel katalogdaki SHA-256 ile karşılaştırılır, en çok 300 KB ve 512 piksel olmalıdır, yeniden kodlanarak saklanır; kurala uymayan kayıtlar gösterilmez.
- **Göndermek:** kendi pet’ine sağ tık → **Workshop’a gönder…** Petglare görseli bir klasöre hazırlar, Gezgin’de seçili gösterir ve GitHub’daki formu tarayıcıda açar. Görseli forma sürükleyip gönderirsin (GitHub hesabı gerekir). Bir bot görseli denetleyip yeniden kodlar ve onay için PR açar; bakımcı birleştirince katalog kendiliğinden güncellenir. Gönderimler herkese açıktır ve CC BY 4.0 ile paylaşılır. Petglare GitHub’a kendisi bağlanmaz ve hiçbir hesap bilgisi tutmaz.

### Arka plan modeli: ölçüm

Yöntemler, doğru cevabı bilinen görsellerde karşılaştırıldı: şeffaf Petglare maskotu, bilgisayardaki 8 Windows duvar kâğıdı fotoğrafının üstüne üç en-boy oranında (3:2, 2:3, 1:1) ve iki konumda yerleştirildi; her görsel 0°, 90°, 180° ve 270° döndürülerek çalıştırıldı (48 × 4). IoU, maskenin doğru cevapla örtüşmesidir.

| Yöntem | IoU ort. | 0° / 90° / 180° / 270° | Ön plan korunan | Yanlış ön plan | Yönelim tutarlılığı | Süre |
|---|---|---|---|---|---|---|
| Hızlı, 320×320 esnetme (varsayılan) | 0,706 | 0,692 / 0,736 / 0,664 / 0,731 | %96,5 | %13,8 | 0,838 | ~210 ms |
| Hızlı, oran korumalı | 0,634 | 0,628 / 0,652 / 0,615 / 0,640 | %95,4 | %20,9 | 0,831 | ~200 ms |
| **Yüksek kalite**, 4 yön, ortalama | **0,732** | 0,732 (hepsi) | %97,4 | %13,7 | 1,000 | ~850 ms |
| Yüksek kalite, 4 yön, en büyük | 0,647 | 0,647 (hepsi) | %98,7 | %19,8 | 1,000 | ~850 ms |
| Yüksek kalite, oran korumalı | 0,672 | 0,672 (hepsi) | %97,5 | %19,0 | 1,000 | ~820 ms |

Sonuç: oran korumalı ön işleme bu modelde daha kötü (fazladan arka planı ön plan sayıyor), bu yüzden kullanılmıyor. Yüksek kalite (esnetme + ortalama) yönelimden bağımsız aynı sonucu veriyor ve ortalamada biraz daha iyi; yaklaşık dört kat sürüyor, bu yüzden isteğe bağlı. Sınırlar: ön plan bir çizgi karakter; insan fotoğrafı içeren bir ölçüm seti yok, yan çekilmiş insan fotoğraflarındaki kazanç ayrıca elle denenmeli. v1.2 model karşılaştırması (aynı set, tek yön; 4 yön satırları Yüksek kalite seçeneğidir):

| Model | IoU ort. | 0° / 90° / 180° / 270° | Ön plan korunan | Yanlış ön plan | Yönelim tutarlılığı | Süre |
|---|---|---|---|---|---|---|
| U²-Net küçük (Hızlı) | 0,706 | 0,692 / 0,736 / 0,664 / 0,731 | %96,5 | %13,8 | 0,838 | ~0,2 sn |
| U²-Net küçük, 4 yön | 0,732 | 0,732 (hepsi) | %97,4 | %13,7 | 1,000 | ~0,9 sn |
| Silueta (sunulmuyor) | 0,726 | 0,724 / 0,704 / 0,743 / 0,733 | %91,6 | %10,1 | 0,832 | ~0,5 sn |
| Silueta, 4 yön | 0,763 | 0,763 (hepsi) | %92,0 | %8,6 | 1,000 | ~2,0 sn |
| **IS-Net genel (Kaliteli)** | **0,798** | 0,760 / 0,768 / 0,805 / 0,861 | **%98,7** | %9,9 | 0,872 | ~1,3 sn |

Silueta ön planın daha büyük kısmını sildiği için sunulmuyor. IS-Net hem en yüksek örtüşmeyi hem en yüksek ön plan korumasını verdi; Kaliteli seçeneği budur. IS-Net'te Yüksek kalite (4 yön) ölçülmedi; çalışır ama yaklaşık 5 saniye sürer.

Ölçümü tekrar çalıştırmak için (klasörde `u2netp.onnx`, `silueta.onnx`, `isnet-general-use.onnx`; olmayanlar atlanır):

```powershell
dotnet run --project tests\Petglare.Tests -c Release -- --bench <model klasörü> Assets\Petglare-Icon-Transparent-1024.png <rapor klasörü>
```

### Bağlantı teşhisi

Menüdeki **Bağlantı teşhisi…** penceresi şunları gösterir: hook’ların kurulu ve bu EXE’ye bağlı olup olmadığı, eksik olaylar, yerel bağlantının dinlenip dinlenmediği, son olayın ne zaman ve hangi projeden geldiği, izlenen oturumlar ve Claude Desktop’ın çalışıp çalışmadığı. **Bağlantıyı test et** hook komutunu Claude’un çalıştıracağı şekilde çalıştırıp olayın maskota ulaştığını doğrular; **Hook’ları kur / onar** eksik olayları ekler veya taşınmış EXE’nin yolunu düzeltir. Petglare açılırken hook’lar eksikse bunu balonda ve bildirimle söyler.

## Claude olmadan deneme

Petglare açıkken aşağıdaki komutla iki projede düşünme, dosya okuma/düzenleme, başarısız komut, onay, tamamlanma ve API hatası durumlarını sırayla görebilirsin.

```powershell
.\Petglare.exe --demo | Out-Host
```

İşlem sırasında üç dakika olay gelmezse balon kapanır; onay bekleme otuz dakika görünür kalır. Altı saattir hareketsiz oturumlar listeden düşer. Menüdeki **Boştaki oturumları temizle** biten ve hareketsiz oturumları hemen çıkarır; çalışan, onay bekleyen veya hata veren oturumlar kalır. Paralel araçlar araç kimliğiyle ayrı izlenir; `Stop`'tan sonra geç gelen araç sonuçları bitmiş bir yanıtı yeniden "çalışıyor" yapmaz. Claude bir yanıtı sen Esc ile durdurduğunda `Stop` göndermez; o oturum üç dakikalık zaman aşımıyla kapanır.

Hook’lar [Claude Code hook belgelerindeki](https://code.claude.com/docs/en/hooks) `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `PermissionRequest`, `Notification` (yalnız onay ve soru bildirimleri), `Stop`, `StopFailure`, `SessionStart` ve `SessionEnd` olaylarını aynı Windows kullanıcısına ait yerel bir named pipe üzerinden iletir. Petglare prompt, dosya içeriği ve ham komut kaydetmez; yalnız olay türünü, araç türünü, dosya adını ve proje klasörünün adını iletir. Ayarlar `%LOCALAPPDATA%\Petglare\position.json` dosyasında tutulur. Başka bir Stop hook’u Claude’a devam etmesini söylerse Petglare bu kararı göremez.

## Kaynaktan derleme

.NET 10 SDK ile proje klasöründe çalıştır:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o .\Teslim
Compress-Archive -Path .\Teslim\Petglare.exe, .\Kur.cmd, .\Hooklari-Kaldir.cmd, .\README.md -DestinationPath .\Teslim\Petglare-Windows-x64.zip -Force
```

Kaynak dosyalarını GitHub reposuna, ZIP dosyasını GitHub Releases’a ekle.
