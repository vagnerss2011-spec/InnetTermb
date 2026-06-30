using System.IO;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

public sealed class VaultRestartTests : IDisposable
{
    private const string Identity = "userA@machine1";

    private readonly string _dir;
    private readonly string _path;

    public VaultRestartTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remoteops-vault-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "vault.json");
    }

    [Fact]
    public async Task Secret_Survives_Process_Restart()
    {
        const string secret = "Restart-Persist-#42"; // pragma: allowlist secret (fixture sintética)
        string envelopeId;

        // "Sessão 1": grava o segredo e descarta as instâncias (simula encerrar o app).
        {
            var file = new FileVaultStore(_path);
            VaultTestContext ctx = VaultTestContext.OverFile(file, Identity);
            SecretEnvelope envelope = await ctx.Vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = "ws-01", CredentialId = "cred-01", ActorUserId = "op" },
                secret.AsMemory());
            envelopeId = envelope.EnvelopeId;
        }

        // "Sessão 2": instâncias novas sobre o mesmo arquivo, mesma identidade (mesmo usuário/máquina).
        {
            var file = new FileVaultStore(_path);
            VaultTestContext ctx = VaultTestContext.OverFile(file, Identity);
            using VaultSecret revealed = await ctx.Vault.RetrieveAsync(
                envelopeId,
                new VaultAccessContext { ActorUserId = "op" });

            Assert.Equal(secret, revealed.RevealString());
        }
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
            // Ambiente de CI efêmero; ignorar falha de limpeza.
        }
    }
}
