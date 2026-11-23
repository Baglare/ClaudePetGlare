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
        Check(sz.Width == 320 && sz.Height > 143, "window grows for three large bubbles: " + sz);
        Check(PetView.WindowSize(new Preferences { MaxBubbles = 1 }) == new Size(260, 143), "one normal bubble keeps the original size");
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

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures;
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
