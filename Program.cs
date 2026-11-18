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
        using var instance = new Mutex(true, $@"Local\{HookPipe.Channel}-window", out var first);
        if (!first) return 0;
        return new Application().Run(new PetWindow());
    }

    private static async Task<int> DemoAsync()
    {
        var pipe = new HookPipe();
        var session = Guid.NewGuid().ToString("N");
        var prompt = "";
        foreach (var (name, tool, pause) in new (string, string?, int)[]
        {
            ("UserPromptSubmit", null, 900),
            ("PreToolUse", "Read", 1600),
            ("PermissionRequest", null, 1400),
            ("PreToolUse", "Bash", 1300),
            ("Stop", null, 5700),
            ("UserPromptSubmit", null, 1000),
            ("StopFailure", null, 0)
        })
        {
            if (name == "UserPromptSubmit") prompt = Guid.NewGuid().ToString("N");
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                hook_event_name = name,
                session_id = session,
                prompt_id = prompt,
                tool_name = tool,
                tool_input = new { file_path = @"C:\Ornek\Program.cs" }
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
