using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Petglare;

internal static class HookSettings
{
    internal const string Marker = "petglare-v1";

    internal static string DefaultPath()
    {
        var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(directory, "settings.json");
    }

    internal static bool Update(string path, bool install)
    {
        using var gate = new Mutex(false, $@"Local\{HookPipe.Channel}-settings");
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(3)))
                throw new IOException("Başka bir Petglare kurulum işlemi sürüyor.");
        }
        catch (AbandonedMutexException) { }

        try
        {
            return UpdateFile(Path.GetFullPath(path), install);
        }
        finally { gate.ReleaseMutex(); }
    }

    private static bool UpdateFile(string path, bool install)
    {
        var executable = Environment.ProcessPath!;
        if (install && !Path.GetFileName(executable).Equals("Petglare.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kurulum için Petglare.exe dosyasını kullanın.");

        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        if (original is null && !install) return false;
        var settings = original is null ? new JsonObject() : JsonNode.Parse(original)!.AsObject();
        if (!settings.ContainsKey("hooks") && !install) return false;
        var hooks = settings["hooks"]?.AsObject() ?? new JsonObject();
        var before = hooks.ToJsonString();

        foreach (var (name, _) in HookNotice.Events)
        {
            if (hooks[name] is JsonArray groups)
            {
                int groupCount = groups.Count;
                for (int index = groups.Count - 1; index >= 0; index--)
                {
                    if (groups[index] is not JsonObject group || group["hooks"] is not JsonArray handlers)
                        continue;
                    bool removed = false;
                    for (int i = handlers.Count - 1; i >= 0; i--)
                    {
                        if (!Ours(handlers[i])) continue;
                        handlers.RemoveAt(i);
                        removed = true;
                    }
                    if (removed && handlers.Count == 0 && group.Count == 1) groups.RemoveAt(index);
                }
                if (groupCount > 0 && groups.Count == 0 && !install) hooks.Remove(name);
            }
            if (install)
            {
                if (hooks[name] is null) hooks[name] = new JsonArray();
                hooks[name]!.AsArray().Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command", ["command"] = executable,
                        ["args"] = new JsonArray("--hook", Marker), ["timeout"] = 2
                    })
                });
            }
        }

        if (before == hooks.ToJsonString()) return false;
        if (hooks.Count == 0) settings.Remove("hooks");
        else if (hooks.Parent is null) settings["hooks"] = hooks;
        var updated = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(path, original, updated + Environment.NewLine);
        return true;
    }

    private static bool Ours(JsonNode? handler) =>
        handler is JsonObject entry && entry["type"] is JsonValue type && type.TryGetValue<string>(out var kind)
        && kind == "command" && entry["args"] is JsonArray arguments && arguments.Count == 2
        && arguments.Select(x => (x as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null)
            .SequenceEqual(new[] { "--hook", Marker });

    private static void WriteAtomically(string path, string? original, string updated)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".petglare-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            if (original is null)
            {
                File.Move(temporary, path);
                return;
            }
            if (!string.Equals(File.ReadAllText(path), original, StringComparison.Ordinal))
                throw new IOException("Claude ayarları işlem sırasında değişti. Kurulumu yeniden çalıştırın.");
            File.Replace(temporary, path, null);
        }
        finally { File.Delete(temporary); }
    }
}
