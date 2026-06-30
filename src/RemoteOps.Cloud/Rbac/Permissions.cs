namespace RemoteOps.Cloud.Rbac;

public static class Permissions
{
    // Inventário
    public const string AssetRead = "asset.read";
    public const string AssetCreate = "asset.create";
    public const string AssetUpdate = "asset.update";
    public const string AssetDelete = "asset.delete";
    public const string AssetMove = "asset.move";
    public const string AssetExport = "asset.export";

    // Credenciais
    public const string CredentialReadMetadata = "credential.readMetadata";
    public const string CredentialCreate = "credential.create";
    public const string CredentialUpdateMetadata = "credential.updateMetadata";
    public const string CredentialRotate = "credential.rotate";
    public const string CredentialUse = "credential.use";
    public const string CredentialReveal = "credential.reveal";
    public const string CredentialGrant = "credential.grant";
    public const string CredentialRevoke = "credential.revoke";

    // Sessões
    public const string SessionSshOpen = "session.ssh.open";
    public const string SessionTelnetOpen = "session.telnet.open";
    public const string SessionRdpOpen = "session.rdp.open";
    public const string SessionMikroTikApi = "session.mikrotik.api";
    public const string SessionMikroTikWinboxOpen = "session.mikrotik.winbox.open";
    public const string SessionMikroTikWinboxPasswordArgument = "session.mikrotik.winbox.passwordArgument";
    public const string SessionNDeskCreateTicket = "session.ndesk.createTicket";
    public const string SessionNDeskView = "session.ndesk.view";
    public const string SessionNDeskControl = "session.ndesk.control";
    public const string SessionNDeskFileTransfer = "session.ndesk.fileTransfer";
    public const string SessionNDeskAdminRequest = "session.ndesk.adminRequest";
    public const string SessionNDeskAdminApprove = "session.ndesk.adminApprove";

    // Administração
    public const string UserInvite = "user.invite";
    public const string UserDisable = "user.disable";
    public const string DeviceRevoke = "device.revoke";
    public const string PolicyUpdate = "policy.update";
    public const string AuditRead = "audit.read";
    public const string AuditExport = "audit.export";
    public const string ReleaseApprove = "release.approve";
    public const string ToolApprove = "tool.approve";

    // Sync interno
    public const string SyncPush = "sync.push";
    public const string SyncPull = "sync.pull";
}
