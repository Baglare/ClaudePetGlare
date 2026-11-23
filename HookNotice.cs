using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Petglare;

internal enum HookEvent
{
    Prompt, Tool, Permission, Stop, Failure, SessionEnd,
    SessionStart, ToolDone, ToolFailed, Attention, Ping
}

internal enum ToolKind { Other, Read, Edit, Command, Search, Web, Agent, Question, Plan, Mcp }

internal sealed record HookNotice(HookEvent Event, string Session, string? Prompt,
    ToolKind Tool = ToolKind.Other, string? FileName = null, string? Project = null)
{
    /// <summary>Claude Code hook events Petglare installs a handler for.</summary>
    internal static readonly (string Name, HookEvent Event)[] Events =
    [
        ("UserPromptSubmit", HookEvent.Prompt), ("PreToolUse", HookEvent.Tool),
        ("PermissionRequest", HookEvent.Permission), ("Stop", HookEvent.Stop),
        ("StopFailure", HookEvent.Failure), ("SessionEnd", HookEvent.SessionEnd),
        ("SessionStart", HookEvent.SessionStart), ("PostToolUse", HookEvent.ToolDone),
        ("PostToolUseFailure", HookEvent.ToolFailed), ("Notification", HookEvent.Attention)
    ];

    /// <summary>Synthetic event used only by the connection test. Claude never sends it.</summary>
    internal const string PingEvent = "PetglarePing";

    internal static string NameOf(HookEvent value) =>
        value == HookEvent.Ping ? PingEvent : Array.Find(Events, e => e.Event == value).Name ?? value.ToString();

    internal static HookNotice? FromClaude(JsonElement payload)
    {
        if (Value(payload, "agent_id") is not null || Value(payload, "session_id") is not { } session)
            return null;

        var name = Value(payload, "hook_event_name");
        var entry = Array.Find(Events, e => e.Name == name);
        if (entry.Name is null && name != PingEvent) return null;
        var kind = entry.Name is null ? HookEvent.Ping : entry.Event;

        // Only the notifications that need the user are forwarded; idle reminders
        // repeat what the completion already said.
        if (kind == HookEvent.Attention
            && Value(payload, "notification_type") is not ("permission_prompt" or "elicitation_dialog"))
            return null;

        var notice = new HookNotice(kind, session, Value(payload, "prompt_id"),
            Project: ProjectName(Value(payload, "cwd")));

        if (kind is HookEvent.Tool or HookEvent.ToolDone or HookEvent.ToolFailed)
        {
            var toolName = Value(payload, "tool_name");
            var tool = toolName switch
            {
                "Read" => ToolKind.Read,
                "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
                "Bash" or "PowerShell" => ToolKind.Command,
                "Glob" or "Grep" => ToolKind.Search,
                "WebFetch" or "WebSearch" => ToolKind.Web,
                "Agent" or "Task" => ToolKind.Agent,
                "AskUserQuestion" => ToolKind.Question,
                "ExitPlanMode" => ToolKind.Plan,
                { } mcp when mcp.StartsWith("mcp__", StringComparison.Ordinal) => ToolKind.Mcp,
                _ => ToolKind.Other
            };
            string? file = null;
            if (tool is ToolKind.Read or ToolKind.Edit && payload.TryGetProperty("tool_input", out var input))
                file = CleanName(Path.GetFileName(Value(input, "file_path") ?? Value(input, "notebook_path")), 52);
            notice = notice with { Tool = tool, FileName = file };
        }
        return notice.IsValid ? notice : null;
    }

    internal bool IsValid => Enum.IsDefined(Event) && Enum.IsDefined(Tool)
        && ValidId(Session) && (Prompt is null || ValidId(Prompt))
        && (FileName is null || (Event is HookEvent.Tool or HookEvent.ToolDone or HookEvent.ToolFailed
            && Tool is ToolKind.Read or ToolKind.Edit && FileName == CleanName(FileName, 52)))
        && (Project is null || Project == CleanName(Project, 32));

    /// <summary>The folder name of a working directory; a Claude worktree reports its repository.</summary>
    internal static string? ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(cwd.Trim());
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // <repo>\.claude\worktrees\<name> is a worktree of <repo>.
            int worktrees = Array.FindLastIndex(parts, p => p.Equals("worktrees", StringComparison.OrdinalIgnoreCase));
            if (worktrees >= 2 && parts[worktrees - 1].Equals(".claude", StringComparison.OrdinalIgnoreCase))
                return CleanName(parts[worktrees - 2], 32);
            return CleanName(parts[^1], 32);
        }
        catch (ArgumentException) { return null; }
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? CleanName(string? name, int limit)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        // Limit a name to one compact line, including control and bidi characters.
        var clean = string.Concat(name.EnumerateRunes().Where(r =>
            System.Text.Rune.GetUnicodeCategory(r) is not
                (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))
            .Take(limit).Select(r => r.ToString()));
        return clean.Length == 0 ? null : clean;
    }

    private static string? Value(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
