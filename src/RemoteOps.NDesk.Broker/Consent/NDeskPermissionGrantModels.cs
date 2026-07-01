namespace RemoteOps.NDesk.Broker.Consent;

public sealed record GrantedBy(string DisplayName, string? WindowsUser, string MachineName);

public sealed record GrantConsentRequest(
    Guid SessionId,
    GrantedBy GrantedBy,
    string Mode,
    List<string> Permissions,
    TimeSpan? Ttl,
    string? ConsentTextVersion);

public enum GrantOutcome
{
    Granted,
    NoActiveTicket,
    PermissionsExceedRequest,
}

public sealed record GrantConsentResult(GrantOutcome Outcome, RemoteOps.Contracts.NDesk.NDeskPermissionGrant? Grant);
