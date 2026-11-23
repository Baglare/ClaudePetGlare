# Petglare — ajan yönergeleri

Petglare, Claude Code hook olaylarını yerel named pipe üzerinden alan ve durumu masaüstünde bir maskotla gösteren Windows 11 WPF uygulamasıdır (.NET 10, `net10.0-windows`). Kullanıcıya dönük belge [README.md](README.md); arayüz ve mesajlar Türkçedir.

## Konum ve hafıza

- Çalışma klasörü: `C:\AvenoxBrain\Projects\Petglare`. Eski `C:\ClaudePetglare` klasörü taşıma öncesi geri dönüş kopyasıdır; orada geliştirme yapma.
- Avenox V3 proje adı `Petglare`. Kalıcı durum notu: `C:\AvenoxBrain\knowledge\concepts\petglare-proje-durumu.md`. Bağlam için `py -3 C:\AvenoxBrain\beyin.py context "konu" --project Petglare`.

## Yapı

| Dosya | Görev |
|---|---|
| `Program.cs` | Giriş: `--hook petglare-v1` (Claude'un çalıştırdığı hook), `--install-hooks`, `--uninstall-hooks`, `--setup`, `--remove-setup`, `--demo`; tek örnek kilidi ve ikinci açılışta gizli maskotu geri getirme |
| `HookNotice.cs` | Claude hook JSON'unu küçük, doğrulanmış bir bildirime çevirir (olay, araç türü, dosya adı, proje adı) |
| `HookPipe.cs` | Aynı Windows kullanıcısına özel named pipe; uzunluk önekli çerçeve, en çok 2048 bayt |
| `HookSettings.cs` | `~/.claude/settings.json` içine Petglare hook'larını atomik ekler/kaldırır, `Inspect` ile durumu raporlar |
| `PetState.cs` | Oturum başına durum makinesi: öncelik (Attention > Error > Working > Complete), zaman aşımları, bildirim kararları |
| `PetView.xaml(.cs)` | Maskot ve alt alta sohbet balonları; masaüstü penceresi ve ayarlar önizlemesi aynı görünümü kullanır |
| `PetWindow.xaml(.cs)` | Saydam, etkinleşmeyen, en üstte pencere: konum, sürükleme/yaslama, tam ekran algılama, sol tık kilidi, tepsi, menü |
| `SettingsWindow.xaml(.cs)` | Kalıcı ayarlar ve canlı önizleme; Uygula masaüstü maskota geçirir |
| `DiagnosticsWindow.cs` | Hook kurulumu, dinleme, son olay, uçtan uca ping testi ve onarım |
| `Preferences.cs` | `%LOCALAPPDATA%\Petglare\position.json` (eski dosyalarla uyumlu) |
| `Appearance.cs`, `TrayIcon.cs`, `AutoStart.cs` | Tema verisi, bildirim alanı simgesi/balon bildirimi, `HKCU\...\Run` |

Maskot çizimleri `PetView.xaml` içindeki vektör yollarıdır; görsel dosya yoktur.

## Derleme, test, yayın

```powershell
dotnet build -c Debug
dotnet run --project tests\Petglare.Tests -- <PNG klasörü>
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o .\Teslim
Compress-Archive -Path .\Teslim\Petglare.exe, .\Kur.cmd, .\Hooklari-Kaldir.cmd, .\README.md -DestinationPath .\Teslim\Petglare-Windows-x64.zip -Force
```

- Test projesi uygulama kaynaklarını doğrudan derler (internal tipler için) ve `ALL PASS` ile biter; çıkış kodu başarısız kontrol sayısıdır. PNG'ler masaüstüne çizilmeden üretilir: maskot halleri, temalar, üç büyük balon ve ayarlar penceresi.
- Hook kurulum testi geçici bir ayar dosyası kullanır; kullanıcının gerçek Claude ayarına dokunmaz.
- `Teslim/` Git dışıdır. Claude hook'ları ve otomatik başlatma `Teslim\Petglare.exe` yolunu gösterir; yayın öncesi çalışan Petglare'ı kapat, yayından sonra yeniden aç.

## Kurallar

- Hook yolu Claude'un her araç çağrısında çalışır: hızlı kalmalı, Claude'un hook sonucunu asla değiştirmemeli (`--hook` her durumda 0 döner).
- Prompt, dosya içeriği, ham komut veya transkript iletme/kaydetme; yalnız olay türü, araç türü, dosya adı ve proje klasörü adı.
- Kullanıcının gerçek `~/.claude/settings.json` dosyasını yalnız kullanıcı isteyince değiştir (kurulum, onarım).
- Kullanıcı oyun oynarken masaüstünde test penceresi açma; görsel kontrolü test projesinin ekran dışı render'larıyla yap.
- Commit/push yalnız kullanıcı isteyince; varsayılan daldaysan önce dal aç.
