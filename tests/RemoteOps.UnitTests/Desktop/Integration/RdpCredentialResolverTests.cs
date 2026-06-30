using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Terminal;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class RdpCredentialResolverTests
{
    private sealed class FixedSecurityContext : ITerminalSecurityContext
    {
        public string ActorUserId { get; init; } = "test-user";
        public string? DeviceId { get; init; }
    }

    [Fact]
    public async Task ResolvePasswordAsync_ReturnsRevealedSecret()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var envelopeId = await vault.SetupAsync("s3cr3t-rdp", "cred-rdp");
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cr-rdp-1",
            Name = "RDP cred",
            Type = "password",
            SecretEnvelopeId = envelopeId,
            Metadata = new CredentialMetadata { Username = "CORP\\admin" },
        });

        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("cr-rdp-1");

        Assert.Equal("s3cr3t-rdp", password);
    }

    [Fact]
    public async Task ResolvePasswordAsync_UnknownCredentialRef_ReturnsNull()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("does-not-exist");

        Assert.Null(password);
    }

    [Fact]
    public async Task ResolvePasswordAsync_NoSecretEnvelope_ReturnsNull()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cr-no-secret",
            Name = "No secret",
            Type = "password",
            Metadata = new CredentialMetadata { Username = "admin" },
        });
        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("cr-no-secret");

        Assert.Null(password);
    }
}
