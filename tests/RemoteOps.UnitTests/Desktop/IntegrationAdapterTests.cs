using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Testes unitários para os métodos novos de ILocalStore e os adaptadores de resolução.
/// Nenhuma sessão remota é aberta.
/// </summary>
public sealed class IntegrationAdapterTests
{
    private readonly InMemoryLocalStore _store = new();

    // GetEndpointAsync ---------------------------------------------------

    [Fact]
    public async Task GetEndpointAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.GetEndpointAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetEndpointAsync_ReturnsEndpoint_WhenFound()
    {
        var endpoint = new Endpoint
        {
            Id = "ep-1",
            AssetId = "asset-1",
            Protocol = "ssh",
            Ipv4 = "10.0.0.1",
            Port = 22,
        };
        await _store.AddEndpointAsync(endpoint);

        var result = await _store.GetEndpointAsync("ep-1");

        Assert.NotNull(result);
        Assert.Equal("ep-1", result.Id);
        Assert.Equal("10.0.0.1", result.Ipv4);
    }

    // GetCredentialRefAsync ----------------------------------------------

    [Fact]
    public async Task GetCredentialRefAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.GetCredentialRefAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCredentialRefAsync_ReturnsCredentialRef_WhenFound()
    {
        var credRef = new CredentialRef
        {
            Id = "cred-1",
            Name = "Credencial SSH",
            Type = "ssh-password",
            Metadata = new CredentialMetadata { Username = "admin" },
            SecretEnvelopeId = "env-abc",
        };
        await _store.AddCredentialRefAsync(credRef);

        var result = await _store.GetCredentialRefAsync("cred-1");

        Assert.NotNull(result);
        Assert.Equal("cred-1", result.Id);
        Assert.Equal("env-abc", result.SecretEnvelopeId);
    }

    // LocalStoreEndpointResolver -----------------------------------------

    [Fact]
    public async Task EndpointResolver_ThrowsInvalidOperation_WhenNotFound()
    {
        var resolver = new LocalStoreEndpointResolver(_store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("missing-ep"));
    }

    [Fact]
    public async Task EndpointResolver_ReturnsEndpoint_WhenFound()
    {
        var endpoint = new Endpoint
        {
            Id = "ep-resolve",
            AssetId = "asset-x",
            Protocol = "telnet",
            Ipv4 = "192.168.1.1",
            Port = 23,
        };
        await _store.AddEndpointAsync(endpoint);

        var resolver = new LocalStoreEndpointResolver(_store);
        var result = await resolver.ResolveAsync("ep-resolve");

        Assert.Equal("ep-resolve", result.Id);
    }

    // StoreCredentialRefResolver -----------------------------------------

    [Fact]
    public async Task CredentialRefResolver_ThrowsInvalidOperation_WhenNotFound()
    {
        var resolver = new StoreCredentialRefResolver(_store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("missing-cred"));
    }

    [Fact]
    public async Task CredentialRefResolver_ReturnsCredentialRef_WhenFound()
    {
        var credRef = new CredentialRef
        {
            Id = "cred-resolve",
            Name = "Admin WinBox",
            Type = "winbox-password",
            Metadata = new CredentialMetadata { Username = "admin" },
            SecretEnvelopeId = "env-xyz",
        };
        await _store.AddCredentialRefAsync(credRef);

        var resolver = new StoreCredentialRefResolver(_store);
        var result = await resolver.ResolveAsync("cred-resolve");

        Assert.Equal("cred-resolve", result.Id);
        Assert.Equal("env-xyz", result.SecretEnvelopeId);
    }

    // AppTerminalSecurityContext -----------------------------------------

    [Fact]
    public void AppTerminalSecurityContext_HasLocalUser()
    {
        var ctx = new AppTerminalSecurityContext();
        Assert.Equal("local-user", ctx.ActorUserId);
        Assert.NotNull(ctx.DeviceId);
    }
}
