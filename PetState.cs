using System;
using System.Collections.Generic;
using System.Linq;

namespace Petglare;

internal enum PetPhase { Idle, Working, Attention, Complete, Badge, Error }

internal enum AlertKind { Attention, Complete, Error }

/// <summary>Something worth telling the user about outside the bubble.</summary>
internal sealed record PetAlert(AlertKind Kind, string? Project, string Message, TimeSpan TurnLength);

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
            case HookEvent.Tool when notice.Tool is ToolKind.Question or ToolKind.Plan:
            {
                bool was = session.Phase == PetPhase.Attention;
                var message = notice.Tool == ToolKind.Question ? "Sana soru soruyor" : "Plan onayı bekleniyor";
                Show(session, PetPhase.Attention, message, now, AttentionTimeout);
                return was ? null : new PetAlert(AlertKind.Attention, session.Project, message, now - session.TurnStarted);
            }
            case HookEvent.Tool:
                Show(session, PetPhase.Working, ToolMessage(notice), now, WorkingTimeout);
                return null;
            case HookEvent.ToolDone:
                if (!session.Warning || now >= session.WarningUntil)
                    Show(session, PetPhase.Working, "Claude düşünüyor", now, WorkingTimeout);
                return null;
            case HookEvent.ToolFailed:
                Show(session, PetPhase.Working, FailureMessage(notice.Tool), now, WorkingTimeout);
                session.Warning = true;
                session.WarningUntil = now + WarningTime;
                return null;
            case HookEvent.Permission or HookEvent.Attention:
            {
                bool was = session.Phase == PetPhase.Attention;
                var message = notice.Event == HookEvent.Permission ? "Onay bekleniyor" : "Yanıtını bekliyor";
                if (was && notice.Event == HookEvent.Attention) return null;
                Show(session, PetPhase.Attention, message, now, AttentionTimeout);
                return was ? null : new PetAlert(AlertKind.Attention, session.Project, message, now - session.TurnStarted);
            }
            case HookEvent.Stop:
            {
                var length = now - session.TurnStarted;
                session.InTurn = false;
                Show(session, PetPhase.Complete, "✓", now, CompleteTime);
                return new PetAlert(AlertKind.Complete, session.Project, "Yanıt tamamlandı", length);
            }
            case HookEvent.Failure:
            {
                var length = now - session.TurnStarted;
                session.InTurn = false;
                Show(session, PetPhase.Error, "Claude API hatası", now, ErrorTime);
                return new PetAlert(AlertKind.Error, session.Project, "Claude API hatası", length);
            }
        }
        return null;
    }

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
                    session.Message = "Claude düşünüyor";
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
            if (session.Phase == PetPhase.Idle && !session.InTurn) session.Prompt = null;
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
