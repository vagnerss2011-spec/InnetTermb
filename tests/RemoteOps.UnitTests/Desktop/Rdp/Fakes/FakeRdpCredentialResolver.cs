using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Desktop.Rdp.Fakes;

internal sealed class FakeRdpCredentialResolver : IRdpCredentialResolver
{
    public string? PasswordToReturn { get; set; } = "s3cr3t-rdp";
    public List<string> RequestedCredentialRefIds { get; } = [];

    public Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default)
    {
        RequestedCredentialRefIds.Add(credentialRefId);
        return Task.FromResult(PasswordToReturn);
    }
}
