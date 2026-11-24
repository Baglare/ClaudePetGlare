using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Petglare;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--hook", HookSettings.Marker])
        {
            try { new HookPipe().ForwardAsync(Console.OpenStandardInput()).GetAwaiter().GetResult(); }
            catch { /* An observer must never change Claude's hook result. */ }
            return 0;
        }

        if (args.Length is 1 or 2 && args[0] is "--install-hooks" or "--uninstall-hooks")
        {
            try
            {
                var path = args.Length == 2 ? args[1] : HookSettings.DefaultPath();
                var changed = HookSettings.Update(path, args[0] == "--install-hooks");
                Report(changed ? $"Hook ayarları güncellendi. {Path.GetFullPath(path)}" : "Hook ayarları zaten güncel.");
                return 0;
            }
            catch (Exception ex)
            {
                Report($"Hook ayarları güncellenemedi. {ex.Message}");
                return 1;
            }
        }

        if (args is ["--demo"]) return DemoAsync().GetAwaiter().GetResult();
        if (args is ["--setup"] or ["--remove-setup"])
        {
            bool install = args[0] == "--setup";
            try
            {
                HookSettings.Update(HookSettings.DefaultPath(), install);
                MessageBox.Show(install
                        ? "Petglare bağlandı. Claude Desktop’ta Code → Local oturumu açabilirsin."
                        : "Petglare hook’ları kaldırıldı.",
                    "Petglare", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Claude ayarları güncellenemedi.\n{ex.Message}",
                    "Petglare", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            return install ? RunPet() : 0;
        }
        if (args.Length != 0)
        {
            Report("Seçenekler  --install-hooks [dosya], --uninstall-hooks [dosya], --demo");
            return 1;
        }

        return RunPet();
    }

    private static int RunPet()
    {
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{HookPipe.Channel}-show");
        using var instance = new Mutex(true, $@"Local\{HookPipe.Channel}-window", out var first);
        if (!first)
        {
            // Bring back a pet that is hidden in the notification area.
            show.Set();
            return 0;
        }
        return new Application().Run(new PetWindow(show));
    }

    private static async Task<int> DemoAsync()
    {
        var pipe = new HookPipe();
        var main = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        var prompts = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var (session, cwd, name, tool, file, pause) in new (string, string, string, string?, string?, int)[]
        {
            (main, @"C:\Projeler\VoxForge", "SessionStart", null, null, 300),
            (main, @"C:\Projeler\VoxForge", "UserPromptSubmit", null, null, 900),
            (main, @"C:\Projeler\VoxForge", "PreToolUse", "Read", "Program.cs", 1300),
            (main, @"C:\Projeler\VoxForge", "PostToolUse", "Read", "Program.cs", 700),
            (other, @"C:\Projeler\Atlas\.claude\worktrees\yeni-ozellik", "UserPromptSubmit", null, null, 400),
            (other, @"C:\Projeler\Atlas\.claude\worktrees\yeni-ozellik", "PreToolUse", "Grep", null, 1200),
            (main, @"C:\Projeler\VoxForge", "PreToolUse", "Edit", "Program.cs", 1300),
            (main, @"C:\Projeler\VoxForge", "PostToolUse", "Edit", "Program.cs", 500),
            (main, @"C:\Projeler\VoxForge", "PreToolUse", "Bash", null, 1000),
            (main, @"C:\Projeler\VoxForge", "PostToolUseFailure", "Bash", null, 2200),
            (main, @"C:\Projeler\VoxForge", "PreToolUse", "Bash", null, 600),
            (main, @"C:\Projeler\VoxForge", "PermissionRequest", null, null, 2600),
            (main, @"C:\Projeler\VoxForge", "PostToolUse", "Bash", null, 900),
            (other, @"C:\Projeler\Atlas\.claude\worktrees\yeni-ozellik", "Stop", null, null, 1500),
            (main, @"C:\Projeler\VoxForge", "Stop", null, null, 5500),
            (main, @"C:\Projeler\VoxForge", "UserPromptSubmit", null, null, 1000),
            (main, @"C:\Projeler\VoxForge", "StopFailure", null, null, 0)
        })
        {
            if (name == "UserPromptSubmit") prompts[session] = Guid.NewGuid().ToString("N");
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                hook_event_name = name,
                session_id = session,
                prompt_id = prompts.TryGetValue(session, out var id) ? id : null,
                cwd,
                tool_name = tool,
                tool_input = new { file_path = file is null ? null : $@"{cwd}\{file}" }
            }));
            if (!await pipe.ForwardAsync(input))
            {
                Report("Petglare penceresini açıp demoyu yeniden çalıştırın.");
                return 1;
            }
            await Task.Delay(pause);
        }
        Report("Demo olayları gönderildi.");
        return 0;
    }

    private static void Report(string message)
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        output.WriteLine(message);
    }
}
