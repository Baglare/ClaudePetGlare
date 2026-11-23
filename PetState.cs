using System;
using System.Collections.Generic;
using System.Linq;

namespace Petglare;

internal enum PetPhase { Idle, Working, Attention, Complete, Badge, Error }

internal enum AlertKind { Attention, Complete, Error }

/// <summary>Something worth telling the user about outside the bubble.</summary>
internal sealed record PetAlert(AlertKind Kind, string? Project, string Message, TimeSpan TurnLength);

/// <summary>A tool that started and has not finished yet.</summary>
internal sealed class RunningTool
{
    internal RunningTool(string id, ToolKind kind, string message, TimeSpan started) =>
        (Id, Kind, Message, Started) = (id, kind, message, started);

    internal string Id { get; }
    internal ToolKind Kind { get; }
    internal string Message { get; }
    internal TimeSpan Started { get; }
    /// <summary>Set while this tool waits for the user (permission, question, plan).</summary>
    internal string? Waiting { get; set; }
}

/// <summary>
/// A wait for the user that no tracked tool accounts for: a permission for a tool Petglare did
/// not see start (by id when Claude sends one, otherwise by kind), or a bare notification.
/// </summary>
internal sealed record PendingWait(string? ToolUse, ToolKind Kind, string Message, bool FromNotification);

/// <summary>The live status of one Claude Code session.</summary>
internal sealed class SessionStatus
{
    internal SessionStatus(string id) => Id = id;

    internal string Id { get; }
    internal string? Project { get; set; }
    internal PetPhase Phase { get; set; }
    internal string Message { get; set; } = "";
    /// <summary>True while a failed tool result is still shown.</summary>
    internal bool Warning { get; set; }
    internal TimeSpan LastEvent { get; set; }
    internal string? Prompt { get; set; }
    internal bool InTurn { get; set; }
    internal TimeSpan TurnStarted { get; set; }
    internal TimeSpan Deadline { get; set; }
    internal TimeSpan WarningUntil { get; set; }
    internal string WarningMessage { get; set; } = "";
    /// <summary>Tools running in parallel, by tool use id; one finishing leaves the others.</summary>
    internal Dictionary<string, RunningTool> Tools { get; } = new(StringComparer.Ordinal);
    /// <summary>Waits no running tool accounts for, oldest first.</summary>
    internal List<PendingWait> Waits { get; } = new();
    internal int UnnamedTools { get; set; }

    internal string Title => Project ?? $"Oturum {Id[..Math.Min(6, Id.Length)]}";

    internal bool IsActive => Phase is PetPhase.Working or PetPhase.Attention;

    internal int Priority => Phase switch
    {
        PetPhase.Attention => 4,
        PetPhase.Error => 3,
        PetPhase.Working => 2,
        PetPhase.Complete => 1,
        _ => 0
    };

    internal string Summary => Phase switch
    {
        PetPhase.Idle => "Boşta",
        PetPhase.Badge => "Tamamlandı",
        PetPhase.Complete => "Tamamlandı",
        _ => Message
    };
}

internal sealed class PetState
{
    private static readonly TimeSpan WorkingTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AttentionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CompleteTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ErrorTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(6);

    private readonly Dictionary<string, SessionStatus> sessions = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<SessionStatus> Sessions => sessions.Values;

    /// <summary>Sessions that have something to say, most important first.</summary>
    internal SessionStatus[] Speaking => sessions.Values
        .Where(s => s.Priority > 0)
        .OrderByDescending(s => s.Priority).ThenByDescending(s => s.LastEvent)
        .ToArray();

    /// <summary>The session the nearest bubble speaks for, if any.</summary>
    internal SessionStatus? Focus => Speaking.FirstOrDefault();

    /// <summary>
    /// The user has looked at Claude, so finished turns no longer need a reminder.
    /// Returns true when anything visible changed.
    /// </summary>
    internal bool ClearFinished()
    {
        bool changed = false;
        foreach (var session in sessions.Values)
        {
            if (session.Phase != PetPhase.Badge) continue;
            session.Phase = PetPhase.Idle;
            session.Message = "";
            changed = true;
        }
        return changed;
    }

    internal bool ShowBadge => sessions.Values.Any(s => s.Phase == PetPhase.Badge);

    internal int ActiveCount => sessions.Values.Count(s => s.IsActive);

    /// <summary>Applies a hook notice and returns an alert when the user may want to know.</summary>
    internal PetAlert? Apply(HookNotice notice, TimeSpan now)
    {
        Advance(now);
        if (notice.Event == HookEvent.Ping) return null;

        if (!sessions.TryGetValue(notice.Session, out var session))
        {
            if (notice.Event == HookEvent.SessionEnd) return null;
            session = new SessionStatus(notice.Session);
            sessions.Add(notice.Session, session);
        }
        if (notice.Project is not null) session.Project = notice.Project;
        session.LastEvent = now;

        switch (notice.Event)
        {
            case HookEvent.SessionStart:
                return null;
            case HookEvent.SessionEnd:
                sessions.Remove(notice.Session);
                return null;
            case HookEvent.Prompt:
                ResetTurn(session);
                session.Prompt = notice.Prompt;
                session.InTurn = true;
                session.TurnStarted = now;
                Show(session, PetPhase.Working, "Claude düşünüyor", now, WorkingTimeout);
                return null;
        }

        // A late notice from an earlier turn must not overwrite the current one.
        if (session.InTurn && session.Prompt is not null && notice.Prompt is not null && notice.Prompt != session.Prompt)
            return null;

        if (!session.InTurn)
        {
            // Stop and failure only mean something for a turn Petglare saw start.
            if (notice.Event is HookEvent.Stop or HookEvent.Failure) return null;
            // Petglare may have started in the middle of a turn.
            session.InTurn = true;
            session.Prompt = notice.Prompt;
            session.TurnStarted = now;
        }

        switch (notice.Event)
        {
            case HookEvent.Tool:
            {
                // Claude moved on, so a bare notification is answered; permissions stay until their tool reports.
                session.Waits.RemoveAll(w => w.FromNotification);
                var id = notice.ToolUse ?? $"#{++session.UnnamedTools}";
                var tool = new RunningTool(id, notice.Tool, ToolMessage(notice), now);
                if (notice.Tool is ToolKind.Question or ToolKind.Plan)
                    tool.Waiting = notice.Tool == ToolKind.Question ? "Sana soru soruyor" : "Plan onayı bekleniyor";
                session.Tools[id] = tool;
                return Refresh(session, now);
            }
            case HookEvent.ToolDone or HookEvent.ToolFailed:
            {
                if (FindTool(session, notice) is { } done) session.Tools.Remove(done.Id);
                // A result for a tool Petglare never saw start ends only that tool's own wait.
                else if (FindWait(session, notice) is { } wait) session.Waits.Remove(wait);
                if (notice.Event == HookEvent.ToolFailed)
                {
                    session.Warning = true;
                    session.WarningUntil = now + WarningTime;
                    session.WarningMessage = FailureMessage(notice.Tool);
                }
                return Refresh(session, now);
            }
            case HookEvent.Permission:
            {
                // With an id, only that tool; without one, only a running tool of the same kind
                // (the oldest, as results arrive in start order). Never an unrelated tool.
                RunningTool? tool = notice.ToolUse is { } id
                    ? session.Tools.GetValueOrDefault(id)
                    : notice.Tool == ToolKind.Other ? null
                    : session.Tools.Values.Where(t => t.Waiting is null && t.Kind == notice.Tool)
                        .OrderBy(t => t.Started).FirstOrDefault();
                if (tool is not null) tool.Waiting = "Onay bekleniyor";
                else session.Waits.Add(new PendingWait(notice.ToolUse, notice.Tool, "Onay bekleniyor", false));
                return Refresh(session, now);
            }
            case HookEvent.Attention:
            {
                // A permission prompt or question notification repeats a wait that is already shown.
                if (session.Tools.Values.Any(t => t.Waiting is not null) || session.Waits.Count > 0) return null;
                session.Waits.Add(new PendingWait(null, ToolKind.Other, "Yanıtını bekliyor", true));
                return Refresh(session, now);
            }
            case HookEvent.Stop:
            {
                var length = now - session.TurnStarted;
                ResetTurn(session);
                session.InTurn = false;
                Show(session, PetPhase.Complete, "✓", now, CompleteTime);
                return new PetAlert(AlertKind.Complete, session.Project, "Yanıt tamamlandı", length);
            }
            case HookEvent.Failure:
            {
                var length = now - session.TurnStarted;
                ResetTurn(session);
                session.InTurn = false;
                Show(session, PetPhase.Error, "Claude API hatası", now, ErrorTime);
                return new PetAlert(AlertKind.Error, session.Project, "Claude API hatası", length);
            }
        }
        return null;
    }

    /// <summary>
    /// The tool a result belongs to: by tool use id; without one, the oldest running tool of
    /// the same kind (results then arrive in start order).
    /// </summary>
    private static RunningTool? FindTool(SessionStatus session, HookNotice notice)
    {
        if (notice.ToolUse is { } id) return session.Tools.GetValueOrDefault(id);
        return session.Tools.Values.Where(t => t.Kind == notice.Tool).OrderBy(t => t.Started).FirstOrDefault();
    }

    /// <summary>The untracked wait a result ends: same tool use id, or without ids the oldest of the same kind.</summary>
    private static PendingWait? FindWait(SessionStatus session, HookNotice notice) => notice.ToolUse is { } id
        ? session.Waits.FirstOrDefault(w => w.ToolUse == id)
        : session.Waits.FirstOrDefault(w => !w.FromNotification && w.ToolUse is null && w.Kind == notice.Tool);

    private static void ResetTurn(SessionStatus session)
    {
        session.Tools.Clear();
        session.Waits.Clear();
        session.UnnamedTools = 0;
    }

    /// <summary>What a session in a turn shows now: a wait first, then a fresh failure, then the newest running tool.</summary>
    private static PetAlert? Refresh(SessionStatus session, TimeSpan now)
    {
        bool was = session.Phase == PetPhase.Attention;
        var waiting = session.Tools.Values.Where(t => t.Waiting is not null).OrderByDescending(t => t.Started)
            .Select(t => t.Waiting).FirstOrDefault() ?? session.Waits.LastOrDefault()?.Message;
        if (waiting is not null)
        {
            bool warning = session.Warning;
            Show(session, PetPhase.Attention, waiting, now, AttentionTimeout);
            session.Warning = warning && now < session.WarningUntil;
            return was ? null : new PetAlert(AlertKind.Attention, session.Project, waiting, now - session.TurnStarted);
        }
        if (session.Warning && now < session.WarningUntil)
        {
            Show(session, PetPhase.Working, session.WarningMessage, now, WorkingTimeout);
            session.Warning = true;
            return null;
        }
        Show(session, PetPhase.Working, WorkingMessage(session), now, WorkingTimeout);
        return null;
    }

    private static string WorkingMessage(SessionStatus session) =>
        session.Tools.Values.OrderByDescending(t => t.Started).Select(t => t.Message).FirstOrDefault() ?? "Claude düşünüyor";

    /// <summary>Moves timed phases forward. Returns true when anything visible changed.</summary>
    internal bool Advance(TimeSpan now)
    {
        bool changed = false;
        foreach (var session in sessions.Values.ToList())
        {
            if (session.Warning && now >= session.WarningUntil)
            {
                session.Warning = false;
                if (session.Phase == PetPhase.Working)
                {
                    session.Message = WorkingMessage(session);
                    changed = true;
                }
            }

            if (session.Phase is PetPhase.Idle or PetPhase.Badge)
            {
                if (now - session.LastEvent > ForgetAfter)
                {
                    sessions.Remove(session.Id);
                    changed = true;
                }
                continue;
            }
            if (now < session.Deadline) continue;

            changed = true;
            session.Warning = false;
            session.Message = "";
            // A quiet working turn keeps its correlation in case a slow turn resumes.
            session.Phase = session.Phase == PetPhase.Complete ? PetPhase.Badge : PetPhase.Idle;
            if (session.Phase == PetPhase.Idle)
            {
                // A timed-out wait or tool is stale; a later tool event starts fresh.
                session.Tools.Clear();
                session.Waits.Clear();
                if (!session.InTurn) session.Prompt = null;
            }
        }
        return changed;
    }

    private static void Show(SessionStatus session, PetPhase phase, string message, TimeSpan now, TimeSpan duration)
    {
        session.Phase = phase;
        session.Message = message;
        session.Warning = false;
        session.Deadline = now + duration;
    }

    private static string ToolMessage(HookNotice notice) => notice.Tool switch
    {
        ToolKind.Read when !string.IsNullOrWhiteSpace(notice.FileName) => $"{notice.FileName} okunuyor",
        ToolKind.Read => "Dosya okunuyor",
        ToolKind.Edit when !string.IsNullOrWhiteSpace(notice.FileName) => $"{notice.FileName} düzenleniyor",
        ToolKind.Edit => "Dosya düzenleniyor",
        ToolKind.Command => "Komut çalıştırılıyor",
        ToolKind.Search => "Dosyalar aranıyor",
        ToolKind.Web => "Web’e bakılıyor",
        ToolKind.Agent => "Alt ajan çalışıyor",
        ToolKind.Mcp => "Bağlı araç çalışıyor",
        _ => "Araç çalışıyor"
    };

    private static string FailureMessage(ToolKind tool) => tool switch
    {
        ToolKind.Command => "Komut başarısız oldu",
        ToolKind.Edit => "Düzenleme başarısız oldu",
        ToolKind.Read => "Dosya okunamadı",
        ToolKind.Web => "Web isteği başarısız",
        _ => "Araç başarısız oldu"
    };
}
