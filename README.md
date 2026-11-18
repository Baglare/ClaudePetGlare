# Petglare

Petglare, Windows 11’de Claude Desktop’ın **Code → Local** oturumlarından gelen durumları gösteren küçük bir masaüstü maskotu.

## Kurulum

Windows 11 x64, Claude Desktop ve [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) gerekir.

1. GitHub Releases’tan `Petglare-Windows-x64.zip` dosyasını indir ve kalıcı bir klasöre çıkar.
2. `Kur.cmd` dosyasına çift tıkla. Kurulum Petglare hook’larını ekler ve maskotu açar.
3. Claude Desktop’ta **Code → Local** oturumu aç.

Sonraki açılışlarda `Petglare.exe` dosyasını çalıştır. Eski bir Petglare açıksa menüden kapatıp yeni EXE’yi aç. Dosyaları taşıdıysan yeni konumdan `Kur.cmd` dosyasını tekrar çalıştır. Hook’ları kaldırmak için `Hooklari-Kaldir.cmd` dosyasına çift tıkla.

Kurulum, Claude’un kullanıcı ayarlarına yazar (`CLAUDE_CONFIG_DIR` tanımlıysa o klasöre, aksi halde `~/.claude/settings.json` dosyasına). Diğer ayarları ve hook’ları korur. Tekrar kurulum Petglare hook’larını çoğaltmaz. Komut satırından özel bir ayar dosyası için `Petglare.exe --install-hooks <dosya>` ve `Petglare.exe --uninstall-hooks <dosya>` seçeneklerini kullanabilirsin.

## Maskotu kullanma

- **Sol tık** Claude Desktop’ı açar. **Sürükleme** konumu değiştirir.
- Maskotun üzerine gelince beliren **− / +** düğmeleri boyutu %80 ile %150 arasında değiştirir.
- Sağ tık menüsündeki **Tamamlanma parıltısını göster** seçeneği, tikten sonra kalan yıldızı açar veya kapatır. Yeşil tik dört saniye görünür.

## Claude olmadan deneme

Petglare açıkken aşağıdaki komutla düşünme, dosya okuma, onay, komut, tamamlanma ve API hatası durumlarını sırayla görebilirsin.

```powershell
.\Petglare.exe --demo | Out-Host
```

Yeşil tik Claude’un ana yanıtının bittiğini gösterir. Dört saniye sonra balon kapanır ve maskotun kenarında yeni isteğe kadar küçük bir yıldız kalır. İşlem sırasında üç dakika olay gelmezse balon kapanır. API hatası beş saniye görünür. Birden fazla Claude oturumu kullanıyorsan balon son gönderdiğin isteği izler.

Hook’lar [Claude Code hook belgelerindeki](https://code.claude.com/docs/en/hooks) olayları aynı Windows kullanıcısına ait yerel bir named pipe üzerinden iletir. Petglare prompt, dosya içeriği ve ham komut kaydetmez. Konum, boyut ve parıltı tercihi `%LOCALAPPDATA%\Petglare\position.json` dosyasında tutulur. Başka bir Stop hook’u Claude’a devam etmesini söylerse Petglare bu kararı göremez.

## Kaynaktan derleme

.NET 10 SDK ile proje klasöründe çalıştır:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o .\Teslim
Compress-Archive -Path .\Teslim\Petglare.exe, .\Kur.cmd, .\Hooklari-Kaldir.cmd, .\README.md -DestinationPath .\Teslim\Petglare-Windows-x64.zip -Force
```

Kaynak dosyalarını GitHub reposuna, ZIP dosyasını GitHub Releases’a ekle.
