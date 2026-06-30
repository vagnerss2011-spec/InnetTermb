using System.IO;
using System.Security.Cryptography;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// Garante o controle do threat model "notebook roubado / outro usuário":
/// o segredo só abre para o mesmo usuário/máquina que o protegeu.
/// </summary>
public sealed class VaultIsolationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public VaultIsolationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remoteops-vault-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "vault.json");
    }

    [Fact]
    public async Task Different_User_Cannot_Open_Secret()
    {
        await AssertForeignIdentityCannotOpen("userA@machine1", "userB@machine1");
    }

    [Fact]
    public async Task Different_Machine_Cannot_Open_Secret()
    {
        await AssertForeignIdentityCannotOpen("userA@machine1", "userA@machine2");
    }

    private async Task AssertForeignIdentityCannotOpen(string owner, string intruder)
    {
        string envelopeId;
        {
            var file = new FileVaultStore(_path);
            VaultTestContext ownerCtx = VaultTestContext.OverFile(file, owner);
            SecretEnvelope envelope = await ownerCtx.Vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = "ws-01", CredentialId = "cred-01", ActorUserId = "owner" },
                "isolation-secret".AsMemory());
            envelopeId = envelope.EnvelopeId;
        }

        // Mesmo armazenamento (ex.: disco roubado), porém identidade DPAPI diferente.
        var foreignFile = new FileVaultStore(_path);
        VaultTestContext intruderCtx = VaultTestContext.OverFile(foreignFile, intruder);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => intruderCtx.Vault.RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = "intruder" }));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }
}
