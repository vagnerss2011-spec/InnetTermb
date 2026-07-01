namespace RemoteOps.Desktop.NDesk;

/// <summary>Idle → AwaitingConsent → Connected → Ended (docs/09 §Consentimento e UX).</summary>
public enum NDeskSessionState
{
    Idle,
    AwaitingConsent,
    Connected,
    Ended,
}
