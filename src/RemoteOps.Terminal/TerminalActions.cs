namespace RemoteOps.Terminal;

public static class TerminalActions
{
    public const string SessionOpened = "terminal.session.opened";
    public const string SessionClosed = "terminal.session.closed";

    /// <summary>Host key não reconhecida — aguarda confirmação do usuário.</summary>
    public const string HostKeyUnknown = "terminal.hostkey.unknown";

    /// <summary>Host key existente foi substituída — evento de segurança crítico (FIX 5).</summary>
    public const string HostKeyChanged = "terminal.hostkey.changed";

    /// <summary>Usuário aceitou a host key.</summary>
    public const string HostKeyAccepted = "terminal.hostkey.accepted";

    /// <summary>Usuário rejeitou a host key; conexão abortada.</summary>
    public const string HostKeyRejected = "terminal.hostkey.rejected";

    /// <summary>Sessão Telnet aberta após consentimento explícito.</summary>
    public const string TelnetConsentGranted = "terminal.telnet.consent.granted";
}
