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
    private const string NotificationMatcher = "permission_prompt|elicitation_dialog";

    internal sealed record Inspection(string Path, bool FileExists, string[] Missing,
        string[] OtherExecutables, string? Error)
    {
        internal bool Installed => Error is null && Missing.Length == 0 && OtherExecutables.Length == 0;
        internal bool Partial => Error is null && Missing.Length < HookNotice.Events.Length;
    }

    /// <summary>Reports which Petglare hooks Claude will run, without changing anything.</summary>
    internal static Inspection Inspect(string? path = null)
    {
        path = System.IO.Path.GetFullPath(path ?? DefaultPath());
        var all = HookNotice.Events.Select(e => e.Name).ToArray();
        if (!File.Exists(path)) return new Inspection(path, false, all, [], null);
        try
        {
            var hooks = JsonNode.Parse(File.ReadAllText(path))?["hooks"] as JsonObject;
            var missing = new System.Collections.Generic.List<string>();
            var others = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = Environment.ProcessPath ?? "";
            foreach (var name in all)
            {
                var ours = (hooks?[name] as JsonArray ?? [])
                    .OfType<JsonObject>().Select(g => g["hooks"]).OfType<JsonArray>()
                    .SelectMany(h => h).Where(Ours).OfType<JsonObject>()
                    .Select(h => (h["command"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : "")
                    .ToList();
                if (ours.Count == 0) { missing.Add(name); continue; }
                foreach (var command in ours)
                    if (!command.Equals(current, StringComparison.OrdinalIgnoreCase)) others.Add(command);
            }
            return new Inspection(path, true, missing.ToArray(), others.ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException)
        {
            return new Inspection(path, true, all, [], ex.Message);
        }
    }

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
                    if (removed && handlers.Count == 0
                        && group.All(property => property.Key is "hooks" or "matcher"))
                        groups.RemoveAt(index);
                }
                if (groupCount > 0 && groups.Count == 0 && !install) hooks.Remove(name);
            }
            if (install)
            {
                if (hooks[name] is null) hooks[name] = new JsonArray();
                var group = new JsonObject();
                // Idle reminders would only start a process that forwards nothing.
                if (name == "Notification") group["matcher"] = NotificationMatcher;
                group["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = executable,
                    ["args"] = new JsonArray("--hook", Marker), ["timeout"] = 2
                });
                hooks[name]!.AsArray().Add(group);
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
