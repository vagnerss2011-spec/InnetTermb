namespace RemoteOps.Cloud.Rbac;

public static class Roles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string MikroTikOperator = "MikroTikOperator";
    public const string NDeskOperator = "NDeskOperator";
    public const string NDeskAdminOperator = "NDeskAdminOperator";
    public const string Auditor = "Auditor";
    public const string ReadOnly = "ReadOnly";
    public const string ReleaseManager = "ReleaseManager";

    private static readonly HashSet<string> OwnerAll = BuildAll();
    private static readonly HashSet<string> AdminAll = BuildAdmin();
    private static readonly HashSet<string> ManagerAll = BuildManager();
    private static readonly HashSet<string> OperatorAll = BuildOperator();
    private static readonly HashSet<string> MikroTikOperatorAll = BuildMikroTikOperator();
    private static readonly HashSet<string> NDeskOperatorAll = BuildNDeskOperator();
    private static readonly HashSet<string> NDeskAdminOperatorAll = BuildNDeskAdminOperator();
    private static readonly HashSet<string> AuditorAll = BuildAuditor();
    private static readonly HashSet<string> ReadOnlyAll = BuildReadOnly();
    private static readonly HashSet<string> ReleaseManagerAll = BuildReleaseManager();

    /// <summary>
    /// Papel reconhecido? Um papel desconhecido numa membership não concede NADA — o membro entra e
    /// não enxerga nada, sem erro nenhum. Por isso o convite valida antes de gravar.
    /// </summary>
    public static bool IsKnown(string role) => PermissionsOf(role).Count > 0;

    /// <summary>
    /// Permissões do papel (vazio se desconhecido). Exposto para o convite comparar poder entre
    /// papéis: quem convida não pode fabricar alguém MAIS poderoso que ele mesmo e ser removido
    /// pela própria criatura em seguida.
    /// </summary>
    public static IReadOnlySet<string> PermissionsOf(string role) =>
        role switch
        {
            Owner => OwnerAll,
            Admin => AdminAll,
            Manager => ManagerAll,
            Operator => OperatorAll,
            MikroTikOperator => MikroTikOperatorAll,
            NDeskOperator => NDeskOperatorAll,
            NDeskAdminOperator => NDeskAdminOperatorAll,
            Auditor => AuditorAll,
            ReadOnly => ReadOnlyAll,
            ReleaseManager => ReleaseManagerAll,
            _ => Empty,
        };

    private static readonly HashSet<string> Empty = [];

    public static bool RoleGrants(string role, string permission) =>
        role switch
        {
            Owner => OwnerAll.Contains(permission),
            Admin => AdminAll.Contains(permission),
            Manager => ManagerAll.Contains(permission),
            Operator => OperatorAll.Contains(permission),
            MikroTikOperator => MikroTikOperatorAll.Contains(permission),
            NDeskOperator => NDeskOperatorAll.Contains(permission),
            NDeskAdminOperator => NDeskAdminOperatorAll.Contains(permission),
            Auditor => AuditorAll.Contains(permission),
            ReadOnly => ReadOnlyAll.Contains(permission),
            ReleaseManager => ReleaseManagerAll.Contains(permission),
            _ => false,
        };

    private static HashSet<string> BuildAll() =>
    [
        Permissions.AssetRead, Permissions.AssetCreate, Permissions.AssetUpdate,
        Permissions.AssetDelete, Permissions.AssetMove, Permissions.AssetExport,
        Permissions.CredentialReadMetadata, Permissions.CredentialCreate,
        Permissions.CredentialUpdateMetadata, Permissions.CredentialRotate,
        Permissions.CredentialUse, Permissions.CredentialReveal,
        Permissions.CredentialGrant, Permissions.CredentialRevoke,
        Permissions.SessionSshOpen, Permissions.SessionTelnetOpen,
        Permissions.SessionRdpOpen, Permissions.SessionMikroTikApi,
        Permissions.SessionMikroTikWinboxOpen, Permissions.SessionMikroTikWinboxPasswordArgument,
        Permissions.SessionNDeskCreateTicket, Permissions.SessionNDeskView,
        Permissions.SessionNDeskControl, Permissions.SessionNDeskFileTransfer,
        Permissions.SessionNDeskAdminRequest, Permissions.SessionNDeskAdminApprove,
        Permissions.UserInvite, Permissions.UserDisable, Permissions.DeviceRevoke,
        Permissions.PolicyUpdate, Permissions.AuditRead, Permissions.AuditExport,
        Permissions.ReleaseApprove, Permissions.ToolApprove,
        Permissions.SyncPush, Permissions.SyncPull,
    ];

    private static HashSet<string> BuildAdmin() =>
    [
        Permissions.AssetRead, Permissions.AssetCreate, Permissions.AssetUpdate,
        Permissions.AssetDelete, Permissions.AssetMove, Permissions.AssetExport,
        Permissions.CredentialReadMetadata, Permissions.CredentialCreate,
        Permissions.CredentialUpdateMetadata, Permissions.CredentialRotate,
        Permissions.CredentialUse, Permissions.CredentialReveal,
        Permissions.CredentialGrant, Permissions.CredentialRevoke,
        Permissions.SessionSshOpen, Permissions.SessionTelnetOpen,
        Permissions.SessionRdpOpen, Permissions.SessionMikroTikApi,
        Permissions.SessionMikroTikWinboxOpen,
        Permissions.SessionNDeskCreateTicket, Permissions.SessionNDeskView,
        Permissions.SessionNDeskControl, Permissions.SessionNDeskFileTransfer,
        Permissions.SessionNDeskAdminApprove,
        Permissions.UserInvite, Permissions.UserDisable, Permissions.DeviceRevoke,
        Permissions.PolicyUpdate, Permissions.AuditRead, Permissions.AuditExport,
        Permissions.ToolApprove,
        Permissions.SyncPush, Permissions.SyncPull,
    ];

    private static HashSet<string> BuildManager() =>
    [
        Permissions.AssetRead, Permissions.AssetCreate, Permissions.AssetUpdate,
        Permissions.AssetDelete, Permissions.AssetMove,
        Permissions.CredentialReadMetadata, Permissions.CredentialCreate,
        Permissions.CredentialUpdateMetadata, Permissions.CredentialRotate,
        Permissions.CredentialUse, Permissions.CredentialGrant, Permissions.CredentialRevoke,
        Permissions.SessionSshOpen, Permissions.SessionRdpOpen,
        Permissions.SessionNDeskCreateTicket, Permissions.SessionNDeskView,
        Permissions.SessionNDeskAdminApprove,
        Permissions.UserInvite, Permissions.AuditRead,
        Permissions.SyncPush, Permissions.SyncPull,
    ];

    private static HashSet<string> BuildOperator() =>
    [
        Permissions.AssetRead,
        Permissions.CredentialReadMetadata, Permissions.CredentialUse,
        Permissions.SessionSshOpen, Permissions.SessionRdpOpen,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildMikroTikOperator() =>
    [
        Permissions.AssetRead,
        Permissions.CredentialReadMetadata, Permissions.CredentialUse,
        Permissions.SessionMikroTikApi, Permissions.SessionMikroTikWinboxOpen,
        Permissions.SessionSshOpen,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildNDeskOperator() =>
    [
        Permissions.AssetRead,
        Permissions.SessionNDeskCreateTicket, Permissions.SessionNDeskView,
        Permissions.SessionNDeskControl, Permissions.SessionNDeskFileTransfer,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildNDeskAdminOperator() =>
    [
        Permissions.AssetRead,
        Permissions.SessionNDeskCreateTicket, Permissions.SessionNDeskView,
        Permissions.SessionNDeskControl, Permissions.SessionNDeskFileTransfer,
        Permissions.SessionNDeskAdminRequest,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildAuditor() =>
    [
        Permissions.AssetRead,
        Permissions.CredentialReadMetadata,
        Permissions.AuditRead, Permissions.AuditExport,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildReadOnly() =>
    [
        Permissions.AssetRead,
        Permissions.SyncPull,
    ];

    private static HashSet<string> BuildReleaseManager() =>
    [
        Permissions.AssetRead,
        Permissions.AuditRead,
        Permissions.ReleaseApprove,
        Permissions.SyncPull,
    ];
}
