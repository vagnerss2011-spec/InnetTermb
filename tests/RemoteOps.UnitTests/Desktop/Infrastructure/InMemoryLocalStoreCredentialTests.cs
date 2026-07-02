using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class InMemoryLocalStoreCredentialTests
{
    [Fact]
    public async Task UpdateCredentialRef_ChangesNameKeepsId()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "old", Type = "password", SecretEnvelopeId = "e1" });
        await store.UpdateCredentialRefAsync(new CredentialRef { Id = "c1", Name = "new", Type = "password", SecretEnvelopeId = "e1" });
        CredentialRef? got = await store.GetCredentialRefAsync("c1");
        Assert.Equal("new", got!.Name);
        Assert.Equal("e1", got.SecretEnvelopeId);
    }
}
