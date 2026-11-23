using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petglare;

internal static class Tests
{
    static int failures;
    static void Check(bool ok, string name) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }

    static HookNotice? Parse(object payload) =>
        HookNotice.FromClaude(JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement);

    [STAThread]
    static int Main(string[] args)
    {
        // Parsing
        var edit = Parse(new { hook_event_name = "PreToolUse", session_id = "s1", cwd = @"C:\Projeler\VoxForge", tool_name = "Edit", tool_input = new { file_path = @"C:\Projeler\VoxForge\App.cs" } });
        Check(edit is { Event: HookEvent.Tool, Tool: ToolKind.Edit, FileName: "App.cs", Project: "VoxForge" }, "edit parsed with project and file");
        var wt = Parse(new { hook_event_name = "UserPromptSubmit", session_id = "s1", cwd = @"C:\Repo\Atlas\.claude\worktrees\x-y" });
        Check(wt?.Project == "Atlas", "worktree maps to repo name: " + wt?.Project);
        Check(Parse(new { hook_event_name = "Notification", session_id = "s1", notification_type = "idle_prompt" }) is null, "idle notification ignored");
        Check(Parse(new { hook_event_name = "Notification", session_id = "s1", notification_type = "permission_prompt" })?.Event == HookEvent.Attention, "permission notification forwarded");
        Check(Parse(new { hook_event_name = "PostToolUseFailure", session_id = "s1", tool_name = "Bash" }) is { Event: HookEvent.ToolFailed, Tool: ToolKind.Command }, "tool failure parsed");
        Check(Parse(new { hook_event_name = "PetglarePing", session_id = "x" })?.Event == HookEvent.Ping, "ping parsed");
        Check(Parse(new { hook_event_name = "Bogus", session_id = "x" }) is null, "unknown event ignored");
        Check(Parse(new { hook_event_name = "UserPromptSubmit", session_id = "x" })?.Event == HookEvent.Prompt, "prompt parsed");
        Check(Parse(new { hook_event_name = "Stop", session_id = "x", agent_id = "a" }) is null, "subagent ignored");
        Check(Parse(new { hook_event_name = "UserPromptSubmit", session_id = "x", cwd = "C:\\" })?.Project is null, "drive root has no project");
        var roundTrip = JsonSerializer.Deserialize<HookNotice>(JsonSerializer.SerializeToUtf8Bytes(edit!));
        Check(roundTrip == edit && roundTrip!.IsValid && JsonSerializer.SerializeToUtf8Bytes(edit!).Length < 2048, "pipe round trip");

        // State machine
        var state = new PetState();
        var t = TimeSpan.Zero;
        HookNotice N(HookEvent e, string s, string? p = "p1", ToolKind k = ToolKind.Other, string? proj = "A") => new(e, s, p, k, null, proj);
        state.Apply(N(HookEvent.Prompt, "a"), t);
        Check(state.Focus?.Message == "Claude düşünüyor", "prompt -> thinking");
        state.Apply(N(HookEvent.Tool, "a", k: ToolKind.Command), t += TimeSpan.FromSeconds(1));
        Check(state.Focus?.Message == "Komut çalıştırılıyor", "tool -> running");
        state.Apply(N(HookEvent.ToolFailed, "a", k: ToolKind.Command), t += TimeSpan.FromSeconds(1));
        Check(state.Focus is { Message: "Komut başarısız oldu", Warning: true }, "tool failure warning");
        state.Advance(t += TimeSpan.FromSeconds(5));
        Check(state.Focus is { Message: "Claude düşünüyor", Warning: false }, "warning expires to thinking");
        state.Apply(N(HookEvent.Prompt, "b", "q1", proj: "B"), t += TimeSpan.FromSeconds(1));
        Check(state.ActiveCount == 2, "two active sessions");
        var alert = state.Apply(N(HookEvent.Permission, "a"), t += TimeSpan.FromSeconds(1));
        Check(alert?.Kind == AlertKind.Attention && state.Focus?.Id == "a", "permission alerts and takes focus");
        Check(state.Apply(N(HookEvent.Attention, "a"), t) is null, "duplicate attention not re-alerted");
        state.Apply(N(HookEvent.Tool, "b", "q1", ToolKind.Read, "B"), t += TimeSpan.FromSeconds(1));
        Check(state.Focus?.Id == "a", "attention outranks newer work");
        state.Apply(N(HookEvent.ToolDone, "a"), t += TimeSpan.FromSeconds(1));
        Check(state.Focus?.Id == "a" && state.Focus.Phase == PetPhase.Working, "after approval most recent working session focused");
        Check(state.Apply(N(HookEvent.Stop, "a", "old"), t) is null && state.Sessions.Count == 2, "stale prompt stop ignored");
        alert = state.Apply(N(HookEvent.Stop, "a"), t += TimeSpan.FromSeconds(10));
        Check(alert is { Kind: AlertKind.Complete } && alert.TurnLength >= TimeSpan.FromSeconds(10), "stop -> complete alert with length");
        Check(state.Focus?.Id == "b", "working session outranks completion");
        state.Apply(N(HookEvent.Stop, "b", "q1", proj: "B"), t += TimeSpan.FromSeconds(1));
        state.Advance(t += TimeSpan.FromSeconds(5));
        Check(state.Focus is null && state.ShowBadge, "completion becomes badge");
        state.Apply(N(HookEvent.SessionEnd, "b"), t);
        Check(state.Sessions.Count == 1, "session end removes session");
        state.Apply(N(HookEvent.Tool, "c", "z", ToolKind.Search, "C"), t);
        Check(state.Focus?.Id == "c", "tool without seen prompt starts a turn");
        state.Apply(N(HookEvent.Tool, "d", "z", ToolKind.Question, "D"), t += TimeSpan.FromSeconds(1));
        Check(state.Focus is { Id: "d", Phase: PetPhase.Attention, Message: "Sana soru soruyor" }, "AskUserQuestion is attention");
        Check(state.Apply(N(HookEvent.Stop, "e"), t) is null, "stop without turn ignored");

        // Stacking and clearing finished chats
        var stack = new PetState();
        var u = TimeSpan.Zero;
        stack.Apply(N(HookEvent.Prompt, "x", "1", proj: "X"), u);
        stack.Apply(N(HookEvent.Prompt, "y", "2", proj: "Y"), u += TimeSpan.FromSeconds(1));
        stack.Apply(N(HookEvent.Permission, "x", "1", proj: "X"), u += TimeSpan.FromSeconds(1));
        Check(stack.Speaking.Select(z => z.Id).SequenceEqual(new[] { "x", "y" }), "speaking ordered by importance");
        stack.Apply(N(HookEvent.Stop, "x", "1", proj: "X"), u += TimeSpan.FromSeconds(1));
        stack.Advance(u += TimeSpan.FromSeconds(5));
        Check(stack.ShowBadge && stack.Speaking.Length == 1, "finished chat leaves a badge, other keeps bubble");
        Check(stack.ClearFinished() && !stack.ShowBadge && stack.Speaking.Length == 1, "looking at Claude clears badge only");
        var sz = PetView.WindowSize(new Preferences { MaxBubbles = 3, BubbleSize = "Büyük" });
        Check(sz.Width == 299 && sz.Height > 143, "window grows for three large bubbles: " + sz);
        Check(new Preferences { BubbleSize = "Küçük" }.BubblePercent == 92 && new Preferences().BubblePercent == 100,
            "old named bubble sizes become percentages");
        var scaled = new Preferences { BubbleSize = "Büyük" };
        scaled.SetBubbleScale(500);
        Check(scaled.BubblePercent == Preferences.MaxBubbleScale && scaled.BubbleSize is null
            && !JsonSerializer.Serialize(scaled).Contains("BubbleSize"), "bubble scale clamps and drops the old name");
        Check(Appearance.Metrics(100).Font == 13 && Appearance.Metrics(130).Height == Math.Round(38 * 1.3),
            "bubble metrics scale from the normal bubble");
        Check(new Preferences { Size = 500 }.MascotSize == 200 && new Preferences { Size = 10 }.MascotSize == 60
            && new Preferences { Opacity = 5 }.OpacityPercent == 30, "mascot size and opacity clamp to slider range");
        Check(PetView.WindowSize(new Preferences { Size = 200, BubbleScale = 85 }).Width >= 89 * 2,
            "window is wide enough for the largest mascot");
        Check(PetView.WindowSize(new Preferences { MaxBubbles = 1 }) == new Size(260, 143), "one normal bubble keeps the original size");
        Check(!new Preferences().HideInFullScreen, "hide in full screen is off by default");
        Check(JsonSerializer.Deserialize<Preferences>("{\"HideInFullScreen\":true}")!.HideInFullScreen
            && !JsonSerializer.Deserialize<Preferences>("{}")!.HideInFullScreen, "a saved choice is kept, a missing one is off");
        var copy = new Preferences { X = 5, Hidden = true };
        copy.CopySettingsFrom(new Preferences { X = 99, Hidden = false, Palette = "Nane", Size = 120 });
        Check(copy.X == 5 && copy.Hidden && copy.Palette == "Nane" && copy.Size == 120, "copy settings keeps position");

        // Hook settings (install requires the process to be named Petglare.exe — the harness is)
        var dir = Path.Combine(Path.GetTempPath(), "petglare-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, "{\"model\":\"x\",\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"other.exe\"}]}]}}");
        var before = HookSettings.Inspect(file);
        Check(!before.Installed && !before.Partial, "inspect: not installed");
        HookSettings.Update(file, true);
        var after = HookSettings.Inspect(file);
        Check(after.Installed, "inspect: installed after update " + string.Join(",", after.Missing));
        var json = File.ReadAllText(file);
        Check(json.Contains("other.exe") && json.Contains("\"model\"") && json.Contains("permission_prompt|elicitation_dialog"), "keeps user hooks, adds notification matcher");
        Check(!HookSettings.Update(file, true), "reinstall idempotent");
        HookSettings.Update(file, false);
        var removed = File.ReadAllText(file);
        Check(!removed.Contains("petglare") && removed.Contains("other.exe") && !removed.Contains("Notification"), "uninstall removes only ours");
        Directory.Delete(dir, true);

        // Parallel tools in one session (tool_use_id)
        var withId = Parse(new { hook_event_name = "PreToolUse", session_id = "s1", tool_name = "Bash", tool_use_id = "toolu_01ABC" });
        Check(withId?.ToolUse == "toolu_01ABC" && withId.IsValid, "tool use id parsed");
        Check(Parse(new { hook_event_name = "PreToolUse", session_id = "s1", tool_name = "Bash", tool_use_id = "bad id/../" })?.ToolUse is null,
            "invalid tool use id dropped");
        Check(Parse(new { hook_event_name = "PermissionRequest", session_id = "s1", tool_name = "Bash", tool_use_id = "toolu_9" }) is
            { Event: HookEvent.Permission, Tool: ToolKind.Command, ToolUse: "toolu_9" }, "permission carries its tool");
        Check(JsonSerializer.Deserialize<HookNotice>(JsonSerializer.SerializeToUtf8Bytes(withId!)) == withId, "tool use id survives the pipe");
        HookNotice T(HookEvent e, ToolKind k, string? id) => new(e, "par", "p1", k, null, "P", id);
        var par = new PetState();
        var pt = TimeSpan.Zero;
        par.Apply(new HookNotice(HookEvent.Prompt, "par", "p1", Project: "P"), pt);
        par.Apply(T(HookEvent.Tool, ToolKind.Command, "a1"), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.Tool, ToolKind.Read, "b1"), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.ToolDone, ToolKind.Read, "b1"), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Message == "Komut çalıştırılıyor", "finishing one tool keeps the other running: " + par.Focus?.Message);
        par.Apply(T(HookEvent.Tool, ToolKind.Edit, "c1"), pt += TimeSpan.FromSeconds(1));
        var parAlert = par.Apply(T(HookEvent.Permission, ToolKind.Edit, "c1"), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.ToolDone, ToolKind.Command, "a1"), pt += TimeSpan.FromSeconds(1));
        Check(parAlert?.Kind == AlertKind.Attention && par.Focus is { Phase: PetPhase.Attention, Message: "Onay bekleniyor" },
            "another tool finishing keeps a pending approval");
        par.Apply(T(HookEvent.ToolFailed, ToolKind.Command, "zz"), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Phase == PetPhase.Attention, "an unknown tool's failure keeps the approval");
        par.Apply(T(HookEvent.ToolDone, ToolKind.Edit, "c1"), pt += TimeSpan.FromSeconds(5));
        Check(par.Focus is { Phase: PetPhase.Working, Message: "Claude düşünüyor" }, "approved tool done -> thinking");
        // Without ids (older Claude Code): results match the oldest running tool of their kind.
        par.Apply(T(HookEvent.Tool, ToolKind.Command, null), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.Tool, ToolKind.Search, null), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.Permission, ToolKind.Command, null), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.ToolDone, ToolKind.Search, null), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Phase == PetPhase.Attention, "without ids a different tool finishing keeps the approval");
        par.Apply(T(HookEvent.ToolDone, ToolKind.Command, null), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Phase == PetPhase.Working, "without ids the approved tool finishing clears it");
        par.Apply(T(HookEvent.Tool, ToolKind.Question, "q9"), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.Tool, ToolKind.Read, "r9"), pt += TimeSpan.FromSeconds(1));
        par.Apply(T(HookEvent.ToolDone, ToolKind.Read, "r9"), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Message == "Sana soru soruyor", "an open question survives other tools");
        par.Apply(new HookNotice(HookEvent.Stop, "par", "p1", Project: "P"), pt += TimeSpan.FromSeconds(1));
        par.Apply(new HookNotice(HookEvent.Prompt, "par", "p2", Project: "P"), pt += TimeSpan.FromSeconds(1));
        Check(par.Focus?.Message == "Claude düşünüyor" && par.Sessions.First().Tools.Count == 0, "a new turn starts with no tools");

        // Permission events naming a file are forwarded, not dropped
        var editPermission = Parse(new { hook_event_name = "PermissionRequest", session_id = "s1", tool_name = "Edit",
            tool_use_id = "toolu_e1", tool_input = new { file_path = @"C:\Proje\App.cs" } });
        Check(editPermission is { Event: HookEvent.Permission, Tool: ToolKind.Edit, FileName: "App.cs", ToolUse: "toolu_e1" }
            && editPermission.IsValid, "edit permission with a file name is valid");
        var readPermission = Parse(new { hook_event_name = "PermissionRequest", session_id = "s1", tool_name = "Read",
            tool_input = new { file_path = @"C:\Proje\notlar.md" } });
        Check(readPermission is { Event: HookEvent.Permission, Tool: ToolKind.Read, FileName: "notlar.md" }, "read permission with a file name is valid");
        Check(JsonSerializer.Deserialize<HookNotice>(JsonSerializer.SerializeToUtf8Bytes(editPermission!)) is { IsValid: true } back
            && back == editPermission, "file permission survives the pipe");
        Check(!(new HookNotice(HookEvent.Stop, "s1", null, ToolKind.Edit, "App.cs").IsValid), "a file name on a non-tool event stays invalid");

        // A permission is never pinned on an unrelated tool, and survives other tools finishing
        HookNotice U(HookEvent e, ToolKind k, string? id) => new(e, "unk", "p1", k, null, "U", id);
        var unk = new PetState();
        var ut = TimeSpan.Zero;
        unk.Apply(new HookNotice(HookEvent.Prompt, "unk", "p1", Project: "U"), ut);
        unk.Apply(U(HookEvent.Tool, ToolKind.Command, "a1"), ut += TimeSpan.FromSeconds(1));
        unk.Apply(U(HookEvent.Permission, ToolKind.Edit, "ghost"), ut += TimeSpan.FromSeconds(1));
        var unkSession = unk.Sessions.First();
        Check(unkSession.Tools["a1"].Waiting is null && unk.Focus?.Phase == PetPhase.Attention,
            "a permission with an unknown id is not pinned on another tool");
        unk.Apply(U(HookEvent.ToolDone, ToolKind.Command, "a1"), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus is { Phase: PetPhase.Attention, Message: "Onay bekleniyor" }, "another tool finishing keeps the unknown tool's approval");
        unk.Apply(U(HookEvent.ToolDone, ToolKind.Edit, "other"), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus?.Phase == PetPhase.Attention, "a result with a different unknown id keeps it too");
        unk.Apply(U(HookEvent.ToolDone, ToolKind.Edit, "ghost"), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus?.Phase == PetPhase.Working && unkSession.Waits.Count == 0, "its own result ends the approval");
        // Without ids: only a running tool of the same kind may take the permission
        unk.Apply(U(HookEvent.Tool, ToolKind.Command, null), ut += TimeSpan.FromSeconds(1));
        unk.Apply(U(HookEvent.Permission, ToolKind.Edit, null), ut += TimeSpan.FromSeconds(1));
        Check(unkSession.Tools.Values.All(t => t.Waiting is null) && unkSession.Waits.Count == 1,
            "an edit permission is not pinned on a running command");
        unk.Apply(U(HookEvent.ToolDone, ToolKind.Command, null), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus?.Phase == PetPhase.Attention, "the command finishing keeps the edit approval");
        unk.Apply(U(HookEvent.Tool, ToolKind.Read, null), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus?.Phase == PetPhase.Attention, "a new tool starting keeps a pending permission");
        unk.Apply(U(HookEvent.ToolDone, ToolKind.Edit, null), ut += TimeSpan.FromSeconds(1));
        Check(unk.Focus is { Phase: PetPhase.Working } && unkSession.Waits.Count == 0, "the edit result ends it");
        unk.Apply(U(HookEvent.Permission, ToolKind.Other, null), ut += TimeSpan.FromSeconds(1));
        Check(unkSession.Tools.Values.All(t => t.Waiting is null), "an unnamed permission is not pinned on the running read");

        // Picture safety: limits apply before pixels are decoded
        Check(Throws(() => PetImage.CheckSize(20_000, 10)) && Throws(() => PetImage.CheckSize(8000, 8000))
            && !Throws(() => PetImage.CheckSize(4000, 4000)), "picture size limits");
        bool hugeRefused;
        try { PetImage.Decode(new MemoryStream(PngHeader(30_000, 30_000))); hugeRefused = false; }
        catch (InvalidDataException ex) { hugeRefused = ex.Message.Contains("büyük"); }
        Check(hugeRefused, "a 30000×30000 PNG header is refused without decoding");
        Check(Throws(() => PetImage.Decode(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))), "garbage is refused");
        var bigPng = new MemoryStream(EncodePng(SolidBitmap(3000, 1500)));
        var scaledDown = PetImage.Decode(bigPng, PetImage.EditSide);
        Check(scaledDown.PixelWidth == PetImage.EditSide, $"large picture decodes straight to edit size ({scaledDown.PixelWidth})");

        // Rendering, off screen
        var app = new Application();
        var w = new PetWindow();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var receive = typeof(PetWindow).GetMethod("Receive", flags)!;
        var apply = typeof(PetWindow).GetMethod("ApplyAppearance", flags)!;
        var prefs = (Preferences)typeof(PetWindow).GetField("prefs", flags)!.GetValue(w)!;
        prefs.Mascot = "Kedi"; prefs.Palette = "Lavanta"; prefs.Bubble = "Gece"; prefs.Opacity = 100;
        prefs.MaxBubbles = 2; prefs.BubbleSize = "Normal"; prefs.Size = 100;
        apply.Invoke(w, null);
        var outDir = args.Length > 0 ? args[0] : ".";
        void Shot(string name)
        {
            var root = (FrameworkElement)w.Content;
            var size = PetView.WindowSize(prefs);
            w.Content = null;
            var host = new Grid { Width = size.Width, Height = size.Height,
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xF0)) };
            host.Children.Add(root);
            host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)size.Width * 2, (int)size.Height * 2, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(host);
            host.Children.Clear();
            w.Content = root;
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(outDir, name + ".png")); enc.Save(fs);
        }
        void Send(object p) => receive.Invoke(w, [Parse(p)!]);
        Send(new { hook_event_name = "UserPromptSubmit", session_id = "s1", prompt_id = "p", cwd = @"C:\VoxForge" });
        Send(new { hook_event_name = "PreToolUse", session_id = "s1", prompt_id = "p", cwd = @"C:\VoxForge", tool_name = "Edit", tool_input = new { file_path = "C:\\VoxForge\\Program.cs" } });
        Shot("r1-edit");
        Send(new { hook_event_name = "UserPromptSubmit", session_id = "s2", prompt_id = "q", cwd = @"C:\Atlas" });
        Send(new { hook_event_name = "PermissionRequest", session_id = "s1", prompt_id = "p", cwd = @"C:\VoxForge" });
        Shot("r2-attention-2sessions");
        Send(new { hook_event_name = "PostToolUseFailure", session_id = "s1", prompt_id = "p", cwd = @"C:\VoxForge", tool_name = "Bash" });
        Shot("r3-failure");
        Send(new { hook_event_name = "Stop", session_id = "s1", prompt_id = "p", cwd = @"C:\VoxForge" });
        Send(new { hook_event_name = "Stop", session_id = "s2", prompt_id = "q", cwd = @"C:\Atlas" });
        Shot("r4-complete");
        prefs.Mascot = "Hayalet"; prefs.Palette = "Nane"; prefs.Bubble = "Açık";
        apply.Invoke(w, null);
        Send(new { hook_event_name = "UserPromptSubmit", session_id = "s1", prompt_id = "p2", cwd = @"C:\VoxForge" });
        Send(new { hook_event_name = "PreToolUse", session_id = "s1", prompt_id = "p2", cwd = @"C:\VoxForge", tool_name = "Grep" });
        Shot("r5-theme");
        prefs.Mascot = "Damla"; prefs.Palette = "Gece"; prefs.Bubble = "Koyu";
        apply.Invoke(w, null);
        Shot("r6-theme2");
        prefs.MaxBubbles = 3; prefs.BubbleSize = "Büyük"; prefs.Mascot = "Kedi"; prefs.Palette = "Lavanta"; prefs.Bubble = "Gece";
        apply.Invoke(w, null);
        Send(new { hook_event_name = "UserPromptSubmit", session_id = "s3", prompt_id = "r", cwd = @"C:\Notlar" });
        Send(new { hook_event_name = "PermissionRequest", session_id = "s3", prompt_id = "r", cwd = @"C:\Notlar" });
        Send(new { hook_event_name = "UserPromptSubmit", session_id = "s2", prompt_id = "q2", cwd = @"C:\Atlas" });
        Shot("r7-three-large");

        var settingsWindow = new SettingsWindow(prefs.Clone(), _ => { }, _ => { }, () => { })
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        settingsWindow.Show();
        var iconSizes = TrayIcon.AppIconDecoder.Frames.Select(f => f.PixelWidth).ToHashSet();
        Check(iconSizes.IsSupersetOf(new[] { 16, 32, 256 }), "app icon has 16/32/256 px frames");
        Check(w.Icon != null && settingsWindow.Icon != null, "windows use the app icon");
        void SettingsShot(string name, double offset)
        {
            var scroller = FindChild<ScrollViewer>(settingsWindow)!;
            scroller.ScrollToVerticalOffset(offset);
            settingsWindow.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var content = (Panel)settingsWindow.Content;
            content.Background = settingsWindow.Background;
            var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(outDir, name + ".png")); enc.Save(fs);
        }
        SettingsShot("s1-settings-top", 0);
        SettingsShot("s2-settings-mid", 620);
        SettingsShot("s3-settings-end", 5000);
        settingsWindow.Close();

        // Pet Workshop: picture tools, the pet library, and the custom pet on the desktop
        var petRoot = Path.Combine(Path.GetTempPath(), "petglare-pets-" + Guid.NewGuid().ToString("N"));
        PetLibrary.Root = petRoot;
        var picture = PetImage.FromBitmap(SamplePicture());
        Check(picture.Width == 200 && picture.Height == 180 && picture.Mask.All(m => m == 255), "picture opens fully kept");
        int erased = picture.EdgeErase(12);
        Check(erased > 200 * 180 / 2 && picture.Mask[0] == 0 && picture.Mask[90 * 200 + 100] == 255,
            $"edge erase removes the plain background only ({erased} px)");
        var b = picture.Bounds();
        Check(Math.Abs(b.X - 50) <= 2 && Math.Abs(b.Width - 100) <= 3 && Math.Abs(b.Y - 40) <= 2, "bounds hug the subject: " + b);
        var beforeBrush = picture.Save();
        picture.Dab(100, 90, 6, 1, erase: true);
        Check(picture.Mask[90 * 200 + 100] == 0, "eraser clears under the brush");
        picture.Dab(100, 90, 6, 1, erase: false);
        Check(picture.Mask[90 * 200 + 100] == 255, "restore brush paints it back");
        picture.Restore(beforeBrush);
        var wand = PetImage.FromBitmap(SamplePicture());
        Check(wand.FloodErase(100, 90, 5, contiguous: true) > 0 && wand.Mask[0] == 255 && wand.Mask[90 * 200 + 100] == 0,
            "magic wand erases the clicked colour region only");
        var trimmed = PetImage.FromBitmap(SamplePicture());
        trimmed.EdgeErase(12);
        Check(trimmed.Trim() && trimmed.Width <= 106 && trimmed.Height <= 106, $"trim crops to the subject ({trimmed.Width}x{trimmed.Height})");
        trimmed.RotateRight();
        trimmed.FlipHorizontal();
        trimmed.Feather(2);
        trimmed.Shrink();
        Check(trimmed.Mask.Any(m => m is > 0 and < 255), "feather leaves a soft edge");

        var savedPet = PetLibrary.Save(null, "  Test\npet  ", picture);
        Check(savedPet.Name == "Testpet" && savedPet.Editable && PetLibrary.List().Count == 1, "pet saved with source and mask");
        var petPng = PetImage.Decode(savedPet.ImagePath);
        Check(Math.Max(petPng.PixelWidth, petPng.PixelHeight) <= PetImage.PetSide && petPng.PixelWidth < 200,
            $"saved pet is trimmed ({petPng.PixelWidth}x{petPng.PixelHeight})");
        var reopened = PetLibrary.LoadForEditing(savedPet)!;
        Check(reopened.Width == picture.Width && reopened.Mask.SequenceEqual(picture.Mask), "editing reopens the exact mask");
        var exportPath = Path.Combine(petRoot, "export" + PetLibrary.Extension);
        PetLibrary.Export(savedPet.Id, exportPath);
        var imported = PetLibrary.Import(exportPath);
        Check(imported.Name == "Testpet" && imported.Id != savedPet.Id && PetLibrary.List().Count == 2, "export then import adds a copy");
        var junk = Path.Combine(petRoot, "junk" + PetLibrary.Extension);
        File.WriteAllText(junk, "not a zip");
        Check(Throws(() => PetLibrary.Import(junk)), "a broken pet file is refused");
        Check(PetLibrary.Find("../evil") is null && PetLibrary.IdOf("custom:x") == "x", "pet ids cannot leave the library");
        PetLibrary.Rename(imported.Id, "İkinci");
        Check(PetLibrary.Find(imported.Id)!.Name == "İkinci", "rename");
        PetLibrary.Delete(imported.Id);
        Check(PetLibrary.List().Count == 1, "delete");

        // Package limits: a small file that unpacks huge, and an oversized file
        var bomb = Path.Combine(petRoot, "bomb" + PetLibrary.Extension);
        using (var zip = System.IO.Compression.ZipFile.Open(bomb, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry(PetLibrary.ImageFile, System.IO.Compression.CompressionLevel.SmallestSize).Open())
            entry.Write(new byte[PetLibrary.MaxImportBytes * 8]);
        Check(new FileInfo(bomb).Length < PetLibrary.MaxPackageBytes && Throws(() => PetLibrary.Import(bomb)),
            "a package that unpacks past the limit is refused");
        var fat = Path.Combine(petRoot, "fat" + PetLibrary.Extension);
        File.WriteAllBytes(fat, new byte[PetLibrary.MaxPackageBytes + 1]);
        Check(Throws(() => PetLibrary.Import(fat)), "an oversized package file is refused");
        var giant = Path.Combine(petRoot, "giant" + PetLibrary.Extension);
        using (var zip = System.IO.Compression.ZipFile.Open(giant, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry(PetLibrary.ImageFile).Open())
            entry.Write(PngHeader(30_000, 30_000));
        Check(Throws(() => PetLibrary.Import(giant)) && PetLibrary.List().Count == 1, "a package claiming a giant picture is refused");

        // A mask computed for an older state is never applied
        var raceImage = PetImage.FromBitmap(SolidBitmap(64, 64));
        long raceRevision = raceImage.Revision;
        Check(WorkshopWindow.MaskStillFits(raceImage, raceImage, raceRevision), "unchanged picture accepts its mask");
        raceImage.FlipHorizontal();
        Check(!WorkshopWindow.MaskStillFits(raceImage, raceImage, raceRevision), "flip invalidates a running mask");
        raceRevision = raceImage.Revision;
        raceImage.RotateRight();
        Check(!WorkshopWindow.MaskStillFits(raceImage, raceImage, raceRevision), "rotating a square picture invalidates it");
        raceRevision = raceImage.Revision;
        raceImage.Dab(10, 10, 4, 1, erase: true);
        Check(!WorkshopWindow.MaskStillFits(raceImage, raceImage, raceRevision), "a brush stroke invalidates it");
        raceRevision = raceImage.Revision;
        var sameSize = PetImage.FromBitmap(SolidBitmap(64, 64));
        Check(!WorkshopWindow.MaskStillFits(sameSize, raceImage, raceRevision), "a new picture of the same size invalidates it");
        var snap = raceImage.Save();
        raceRevision = raceImage.Revision;
        raceImage.Restore(snap);
        Check(!WorkshopWindow.MaskStillFits(raceImage, raceImage, raceRevision), "undo invalidates it");

        // A bad model is detected and can be repaired
        var realModel = BackgroundRemover.ModelPath;
        var badModel = Path.Combine(petRoot, "u2netp.onnx");
        BackgroundRemover.ModelPath = badModel;
        Check(BackgroundRemover.Status() == BackgroundRemover.ModelStatus.Missing, "missing model");
        File.WriteAllBytes(badModel, new byte[BackgroundRemover.ModelBytes]);
        Check(BackgroundRemover.Status() == BackgroundRemover.ModelStatus.Corrupt, "right size but wrong checksum is corrupt");
        bool refused;
        try { BackgroundRemover.Predict(PetImage.FromBitmap(SolidBitmap(32, 32))); refused = false; }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "a corrupt model is never loaded");
        File.WriteAllBytes(badModel, new byte[100]);
        Check(BackgroundRemover.Status() == BackgroundRemover.ModelStatus.Corrupt, "truncated model is corrupt");
        BackgroundRemover.DeleteModel();
        Check(!File.Exists(badModel) && BackgroundRemover.Status() == BackgroundRemover.ModelStatus.Missing, "repair removes the bad model");
        BackgroundRemover.ModelPath = realModel;

        prefs.MaxBubbles = 2; prefs.SetBubbleScale(100); prefs.Mascot = savedPet.Key; prefs.Size = 100;
        apply.Invoke(w, null);
        Check(w.Pet.Custom.Visibility == Visibility.Visible && w.Pet.Vector.Visibility == Visibility.Collapsed,
            "custom pet replaces the vector mascot");
        Shot("r8-custom-pet");
        prefs.Mascot = "custom:missing";
        apply.Invoke(w, null);
        Check(w.Pet.Vector.Visibility == Visibility.Visible, "a missing pet falls back to the cat");
        prefs.Mascot = savedPet.Key; prefs.Size = 160; prefs.SetBubbleScale(125);
        apply.Invoke(w, null);
        Shot("r9-custom-large");

        var customSettings = new SettingsWindow(prefs.Clone(), _ => { }, _ => { }, () => { })
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        settingsWindow = customSettings;
        customSettings.Show();
        SettingsShot("s4-settings-custom", 0);
        customSettings.Close();

        var workshop = new WorkshopWindow(savedPet, _ => { }, prefs)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Width = 1120, Height = 720
        };
        workshop.Show();
        workshop.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var workshopContent = (Panel)workshop.Content;
        workshopContent.Background = workshop.Background;
        var wbmp = new RenderTargetBitmap((int)workshopContent.ActualWidth, (int)workshopContent.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        wbmp.Render(workshopContent);
        var wenc = new PngBitmapEncoder(); wenc.Frames.Add(BitmapFrame.Create(wbmp));
        using (var fs = File.Create(Path.Combine(outDir, "w1-workshop.png"))) wenc.Save(fs);
        Check(workshop.Empty.Visibility == Visibility.Collapsed && workshop.SaveButton.IsEnabled == false,
            "workshop opens an existing pet ready to edit");
        workshop.Close();
        var emptyWorkshop = new WorkshopWindow(null, _ => { }, prefs)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        emptyWorkshop.Show();
        Check(emptyWorkshop.Empty.Visibility == Visibility.Visible && !emptyWorkshop.SaveButton.IsEnabled, "new workshop waits for a picture");
        emptyWorkshop.Close();

        // Quitting with an unsaved Workshop: the mascot closes only if the Workshop does
        var unsaved = new WorkshopWindow(null, _ => { }, prefs)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        unsaved.Show();
        typeof(WorkshopWindow).GetMethod("Load", flags)!.Invoke(unsaved, [PetImage.FromBitmap(SamplePicture()), true]);
        typeof(PetWindow).GetField("workshop", flags)!.SetValue(w, unsaved);
        unsaved.Confirm = _ => false;
        Check(unsaved.HasUnsavedChanges && !w.CloseWorkshop() && unsaved.IsVisible, "kept unsaved work stops the mascot from closing");
        unsaved.Confirm = _ => true;
        Check(w.CloseWorkshop() && !unsaved.IsVisible, "discarded work lets the mascot close");
        typeof(PetWindow).GetField("workshop", flags)!.SetValue(w, null);
        Directory.Delete(petRoot, true);

        // Background model: only when a local copy is given (second argument) or already installed.
        if (args.Length > 1) BackgroundRemover.ModelPath = args[1];
        if (BackgroundRemover.ModelPresent)
        {
            var aiPicture = PetImage.FromBitmap(SamplePicture());
            var aiMask = BackgroundRemover.Predict(aiPicture);
            int center = aiMask[90 * 200 + 100], corner = aiMask[5 * 200 + 5];
            Check(aiMask.Length == 200 * 180 && center > 200 && corner < 40,
                $"background model keeps the subject (centre {center}) and drops the corner ({corner})");
            aiPicture.IntersectMask(aiMask);
            var aiEnc = new PngBitmapEncoder(); aiEnc.Frames.Add(BitmapFrame.Create(aiPicture.Compose()));
            using var aiFile = File.Create(Path.Combine(outDir, "a1-ai-mask.png")); aiEnc.Save(aiFile);
        }
        else Console.WriteLine("SKIP background model not present");

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures;
    }

    /// <summary>A purple blob with eyes on a plain light-blue background, 200×180.</summary>
    static BitmapSource SamplePicture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 230, 255)), null, new Rect(0, 0, 200, 180));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(150, 110, 220)), null, new Point(100, 90), 50, 50);
            dc.DrawEllipse(Brushes.Black, null, new Point(82, 82), 6, 9);
            dc.DrawEllipse(Brushes.Black, null, new Point(118, 82), 6, 9);
        }
        var bmp = new RenderTargetBitmap(200, 180, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    static BitmapSource SolidBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 120; pixels[i + 2] = 90; pixels[i + 3] = 255; }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    static byte[] EncodePng(BitmapSource bitmap)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A PNG whose header claims the given size but holds almost no data.</summary>
    static byte[] PngHeader(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body);
            var crc = BitConverter.GetBytes(Crc32(body)); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        var w = BitConverter.GetBytes(width); Array.Reverse(w); w.CopyTo(ihdr, 0);
        var h = BitConverter.GetBytes(height); Array.Reverse(h); h.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk("IHDR", ihdr);
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
