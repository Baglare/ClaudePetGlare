# Petglare

**English** · [Türkçe](README.tr.md)

![Petglare](Assets/Petglare-README-Banner-2400x750.png)

A customizable desktop companion for **Claude Desktop → Code → Local** on Windows. Petglare sits on your desktop and shows what Claude is doing, including when it needs your attention or finishes a response. It is an independent community project, not an official Anthropic product.

[Download the latest release](https://github.com/Baglare/ClaudePetGlare/releases/latest) · [Community Workshop](https://github.com/Baglare/Petglare-Workshop) · [Report an issue](https://github.com/Baglare/ClaudePetGlare/issues)

> The current application interface is in Turkish. English documentation is provided; full English UI localization is not yet available.

## Features

- Live status bubbles for thinking, file reads and edits, commands, searches, approvals, errors and completed responses.
- Multiple Claude sessions, project names, parallel tool tracking, configurable Windows notifications and sounds.
- Always-on-top movable mascot, customizable size, opacity, colors and bubbles; multi-monitor placement and optional hiding in fullscreen.
- **Pet Workshop:** turn your own PNG or photo into a mascot with a local editor, live color-based removal preview, protect/erase/restore brushes, cropping, rotation and undo.
- Optional local AI background removal: fast U²-Net and higher-quality IS-Net; optional four-orientation inference.
- Share pets as .petglare files or browse the opt-in community catalog and submit a pet for maintainer review.
- Connection diagnostics and optional automatic repair of missing paths after moving the installation.

## Installation

Requirements: Windows 11 x64, Claude Desktop with local Code sessions, and the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Download **Petglare-Windows-x64.zip** from [Releases](https://github.com/Baglare/ClaudePetGlare/releases/latest).
2. Extract the entire ZIP to a folder of your choice. Do not run the EXE from inside the archive.
3. Double-click `Kur.cmd` (Install). It registers Petglare's Claude hooks and starts the mascot. You may opt into automatic path repair for later moves.
4. Open Claude Desktop → Code → Local and send a prompt.

To upgrade, exit the old Petglare from its right-click menu, extract the new package, and run `Kur.cmd` or use **Bağlantı teşhisi → Hook'ları kur / onar** (Connection diagnostics → Install/repair hooks). To remove its hooks, run `Hooklari-Kaldir.cmd`.

If you opted into automatic path repair during setup or in **Ayarlar → Ekranda kalma → Taşınan bağlantıları otomatik onar** (Settings → On-screen behavior → Automatically repair moved paths), launch the EXE from its new folder after a move. Petglare repairs its own obsolete hook and Windows startup paths only if the old executable is missing. Existing copies and other tools' hooks are left alone. Without consent, repair remains manual. Settings, custom pets and downloaded models stay under `%LOCALAPPDATA%\Petglare`.

## Usage

Left-click the mascot to open Claude Desktop; drag it to reposition it. Hovering over the mascot shows nothing; change its size with **Ayarlar → Maskot boyutu** (Settings → Mascot size). Right-click the mascot or tray icon for sessions, **Ayarlar** (Settings), **Pet Workshop**, connection diagnostics and exit. The settings window offers live previews; choose **Uygula** (Apply) to save changes. The tray icon toggles mascot visibility. Right-click → **Boştaki oturumları temizle** clears inactive sessions without clearing active ones.

| Status | Meaning |
|---|---|
| Claude düşünüyor | Claude is thinking |
| Dosya okunuyor / düzenleniyor | Reading / editing a file |
| Komut çalıştırılıyor | Running a command |
| Onay bekleniyor / Sana soru soruyor | Waiting for approval / your answer |
| ✓ | Response finished |
| Claude API hatası | API error |

Completion is displayed for four seconds; a small reminder remains until Claude is foregrounded. Working status expires after three minutes without events, attention after 30 minutes, inactive sessions after six hours. Multiple active sessions can display up to three stacked bubbles. Notifications and sound are configurable; completion notifications are for responses lasting at least ten seconds.

### Appearance and fullscreen

Choose a built-in cat, drop or ghost, or a custom pet. Mascot scale is 60–200%, opacity 30–100%, bubble scale 85–130%. The mascot can be dragged over the Windows taskbar and snapped to monitor edges. The always-on-top option works with borderless fullscreen applications; exclusive fullscreen can prevent ordinary Windows overlays from appearing. Fullscreen hiding and click locking are optional.

## Make your own pet

Open **Pet Workshop…** or use the **+** tile in **Ayarlar → Şekil** (Settings → Shape). Open, paste or drag in an image (up to 64 MB and 40 megapixels, downscaled to an editing size of 1024 px).

- For plain backgrounds, use **Önizle: kenardan sil** (Preview edge removal); adjust perceptual color tolerance or pick a background color. Pink indicates what will be removed; click Apply to commit.
- Use the **Koru** (Protect) brush to preserve important regions. Erase/Restore, magic wand, crop, rotation, edge smoothing and undo/redo are available.
- For photos, choose **Hızlı** (Fast, U²-Net small, about 4.4 MiB) or **Kaliteli** (IS-Net, about 170 MiB). The model is downloaded with consent, checked by SHA-256 and run locally. The optional high-quality mode combines four rotated inferences. It is slower and does not guarantee better results on every photo.
- Save the pet and select it in the shape settings. Export/import .petglare files to share directly.

The detailed benchmark methodology, results, shortcuts and technical explanations are available in the [Turkish reference](README.tr.md#arka-plan-modeli-ölçüm). Models are not included in the ZIP.

## Community Workshop

Enable **Ayarlar → Workshop → Workshop'a bağlan (internet)** to browse the [public catalog](https://github.com/Baglare/Petglare-Workshop). This switch is off by default. Search for a pet and click **Ekle** (Add). The catalog and images are fetched from GitHub; images are size-checked, SHA-256 verified and re-encoded.

To submit your own pet, right-click it → **Workshop'a gönder…**. Petglare prepares the image and opens a GitHub submission form in your browser. A GitHub account is required for submission. A bot validates the PNG and opens a PR for maintainer approval. Only approved pets enter the catalog. Submissions are public and shared under CC BY 4.0; submit only images you have the right to share.

## Privacy and limitations

Claude hooks communicate through a same-user local named pipe. Petglare forwards event type, tool type, file name and project folder name, not prompts, file contents or raw commands. AI image processing runs on your PC; optional model downloads and explicitly enabled Workshop browsing use the internet. Workshop submissions are made separately in your browser. Petglare does not store GitHub account credentials. It observes Claude events and does not control Claude's decisions; an interrupted turn may expire via its timeout.

## Build from source

Use the .NET 10 SDK on Windows:

```powershell
dotnet build -c Release
New-Item -ItemType Directory -Force .\test-renders | Out-Null
dotnet run --project tests\Petglare.Tests -c Release -- .\test-renders
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

Optional model tests require a local model file; otherwise they are skipped. See [AGENTS.md](AGENTS.md) and the [Turkish technical guide](README.tr.md) for more.

## License

Petglare's original source code is available under the [MIT License](LICENSE). Third-party runtime components and model sources have their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Community submissions are governed by the Workshop's CC BY 4.0 policy.
