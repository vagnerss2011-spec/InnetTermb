namespace RemoteOps.Contracts;

/// <summary>
/// Authorizes Telnet connections per group/endpoint. Telnet is disabled by default;
/// groups must be explicitly allowed.
/// </summary>
public interface ITelnetPolicy
{
    /// <param name="groupId">The group the endpoint belongs to.</param>
    /// <param name="host">Target host (for audit).</param>
    /// <returns>True when the group is authorized for Telnet.</returns>
    Task<bool> IsTelnetAllowedAsync(string groupId, string host, CancellationToken ct = default);
}
