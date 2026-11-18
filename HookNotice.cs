using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Petglare;

internal enum HookEvent { Prompt, Tool, Permission, Stop, Failure, SessionEnd }
internal enum ToolKind { Other, Read, Edit, Command, Search }

internal sealed record HookNotice(HookEvent Event, string Session, string? Prompt,
    ToolKind Tool = ToolKind.Other, string? FileName = null)
{
    internal static readonly (string Name, HookEvent Event)[] Events =
    [
        ("UserPromptSubmit", HookEvent.Prompt), ("PreToolUse", HookEvent.Tool),
        ("PermissionRequest", HookEvent.Permission), ("Stop", HookEvent.Stop),
        ("StopFailure", HookEvent.Failure), ("SessionEnd", HookEvent.SessionEnd)
    ];

    internal static HookNotice? FromClaude(JsonElement payload)
    {
        if (Value(payload, "agent_id") is not null || Value(payload, "session_id") is not { } session)
            return null;

        var entry = Array.Find(Events, entry => entry.Name == Value(payload, "hook_event_name"));
        if (entry.Name is null) return null;
        var prompt = Value(payload, "prompt_id");
        var notice = new HookNotice(entry.Event, session, prompt);
        if (entry.Event != HookEvent.Tool) return notice.IsValid ? notice : null;

        var tool = Value(payload, "tool_name") switch
        {
            "Read" => ToolKind.Read,
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
            "Bash" or "PowerShell" => ToolKind.Command,
            "Glob" or "Grep" => ToolKind.Search,
            _ => ToolKind.Other
        };
        string? file = null;
        if (tool == ToolKind.Read && payload.TryGetProperty("tool_input", out var input))
            file = CleanName(Path.GetFileName(Value(input, "file_path")));

        notice = notice with { Tool = tool, FileName = file };
        return notice.IsValid ? notice : null;
    }

    internal bool IsValid => Enum.IsDefined(Event) && Enum.IsDefined(Tool)
        && ValidId(Session) && (Prompt is null || ValidId(Prompt))
        && (FileName is null || (Event == HookEvent.Tool && Tool == ToolKind.Read && FileName == CleanName(FileName)));

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        // Limit a filename to one compact line, including control and bidi characters.
        return string.Concat(name.EnumerateRunes().Where(r =>
            System.Text.Rune.GetUnicodeCategory(r) is not
                (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))
            .Take(52).Select(r => r.ToString()));
    }

    private static string? Value(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
