// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Media;

namespace AppleRPC;

/// <summary>The few UI strings, in Italian (default) and English.</summary>
public sealed class Strings
{
    private readonly bool _it;
    public Strings(string language) => _it = language != "en";

    public string StartWithWindows => _it ? "Avvia con Windows" : "Start with Windows";
    public string OpenGitHub => _it ? "Apri su GitHub" : "Open on GitHub";
    public string OpenConfig => _it ? "Apri configurazione" : "Open settings file";
    public string OpenLogs => _it ? "Apri cartella log" : "Open log folder";
    public string Exit => _it ? "Esci" : "Exit";

    public string StartupFailed => _it ? "Impossibile modificare l'avvio automatico." : "Could not change the startup setting.";
    public string NeedsSetupBalloon => _it
        ? "Imposta il tuo Discord Application ID in appsettings.json (vedi README), poi riavvia l'app."
        : "Set your Discord Application ID in appsettings.json (see the README), then restart the app.";

    public string Status(AppStatus s)
    {
        switch (s.Kind)
        {
            case StatusKind.NotConfigured:
                return _it ? "⚠ Configura l'Application ID" : "⚠ Application ID missing";
            case StatusKind.WaitingForAppleMusic:
                return _it ? "In attesa di Apple Music…" : "Waiting for Apple Music…";
            case StatusKind.Playing:
                return $"▶ {s.Track!.Title} — {s.Track.Artist}";
            default:
                return (_it ? "⏸ In pausa: " : "⏸ Paused: ") + $"{s.Track!.Title}";
        }
    }

    public string Discord(AppStatus s)
    {
        if (s.Error is not null) return (_it ? "Discord: errore – " : "Discord: error – ") + s.Error;
        return s.Discord switch
        {
            Presence.PresenceState.Connected => _it ? $"Discord: connesso{(s.DiscordUser is null ? "" : " come " + s.DiscordUser)}" : $"Discord: connected{(s.DiscordUser is null ? "" : " as " + s.DiscordUser)}",
            _ => s.Kind is StatusKind.Playing or StatusKind.Paused
                ? (_it ? "Discord: non in esecuzione, riprovo…" : "Discord: not running, retrying…")
                : (_it ? "Discord: in attesa" : "Discord: idle"),
        };
    }
}
