using System;

namespace Petglare;

internal enum PetPhase { Idle, Working, Complete, Badge, Error }

internal sealed class PetState
{
    private HookNotice? turn;
    private TimeSpan deadline;
    internal PetPhase Phase { get; private set; }
    internal string Message { get; private set; } = "";

    internal void Apply(HookNotice notice, TimeSpan now)
    {
        Advance(now);
        if (notice.Event == HookEvent.Prompt)
        {
            turn = notice;
            Show(PetPhase.Working, "Claude düşünüyor", now, 180);
            return;
        }

        if (turn is null || notice.Session != turn.Session
            || (turn.Prompt is not null && notice.Prompt is not null && notice.Prompt != turn.Prompt))
            return;

        switch (notice.Event)
        {
            case HookEvent.Tool:
                Show(PetPhase.Working, notice.Tool switch
                {
                    ToolKind.Read when !string.IsNullOrWhiteSpace(notice.FileName) => $"{notice.FileName} okunuyor",
                    ToolKind.Read => "Dosya okunuyor",
                    ToolKind.Edit => "Dosya düzenleniyor",
                    ToolKind.Command => "Komut çalıştırılıyor",
                    ToolKind.Search => "Dosyalar aranıyor",
                    _ => "Araç çalışıyor"
                }, now, 180);
                break;
            case HookEvent.Permission:
                Show(PetPhase.Working, "Onay bekleniyor", now, 180);
                break;
            case HookEvent.Stop:
                turn = null;
                Show(PetPhase.Complete, "✓", now, 4);
                break;
            case HookEvent.Failure:
                turn = null;
                Show(PetPhase.Error, "Claude API hatası", now, 5);
                break;
            case HookEvent.SessionEnd:
                Reset();
                break;
        }
    }

    internal bool Advance(TimeSpan now)
    {
        if (Phase is PetPhase.Idle or PetPhase.Badge || now < deadline) return false;

        if (Phase == PetPhase.Complete)
        {
            Phase = PetPhase.Badge;
            Message = "";
        }
        else if (Phase == PetPhase.Working)
        {
            // Keep the correlation in memory if a slow but still active turn resumes.
            Phase = PetPhase.Idle;
            Message = "";
        }
        else Reset();
        return true;
    }

    private void Reset()
    {
        turn = null;
        Phase = PetPhase.Idle;
        Message = "";
    }

    private void Show(PetPhase phase, string message, TimeSpan now, int seconds)
    {
        Phase = phase;
        Message = message;
        deadline = now + TimeSpan.FromSeconds(seconds);
    }
}
