using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// O que a chave do time muda no COFRE, não só no chaveiro: um envelope selado sob a WK só abre com
/// a WK. E, porque os envelopes do time nascem num esquema NOVO, é aqui que o AAD passa a prender
/// também o <c>CredentialId</c> e o <c>Algorithm</c> — os dois campos que hoje ficam de fora e que
/// um servidor malicioso poderia usar para RE-ASSOCIAR um envelope a outra credencial (o colega
/// veria a senha do equipamento X sob o equipamento Y).
/// </summary>
public sealed class WkVaultIsolationTests
{
    private const string Workspace = "ws-time";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    private static CredentialVault VaultOver(ICredentialStore store, IWorkspaceKeyRing ring) =>
        new(store, ring, NullVaultAuditSink.Instance);

    /// <summary>
    /// <b>Prova do isolamento.</b> O mesmo cofre, a mesma AMK, o mesmo workspace: quem tem a WK
    /// abre; quem só sabe derivar a WDK da AMK não abre. É esta assimetria que permite entregar o
    /// cofre do time a um colega sem entregar a conta.
    /// </summary>
    [Fact]
    public async Task EnvelopeSobAWk_AbreComAWk_ENaoAbreComAWdkDerivada()
    {
        byte[] amk = Amk();
        var store = new InMemoryCredentialStore();
        using var wkRing = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);
        using var amkRing = new AmkWorkspaceKeyRing(amk);

        CredentialVault doTime = VaultOver(store, wkRing);
        SecretEnvelope envelope = await doTime.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "cred-01", ActorUserId = "dono" },
            "senha-do-cliente".AsMemory());

        Assert.Equal(VaultAlgorithms.WkRootedV1, envelope.Algorithm);

        using (VaultSecret aberto = await doTime.RetrieveAsync(
            envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "dono" }))
        {
            Assert.Equal("senha-do-cliente", aberto.RevealString());
        }

        CredentialVault derivado = VaultOver(store, amkRing);
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => derivado.RetrieveAsync(envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "outro" }));
    }

    /// <summary>
    /// A recíproca: o que está selado sob a AMK NÃO abre com a WK. Sem isso, "está no cofre do time"
    /// e "está no cofre pessoal" seriam a mesma coisa na hora de decifrar.
    /// </summary>
    [Fact]
    public async Task EnvelopeSobAAmk_NaoAbreComAWk()
    {
        byte[] amk = Amk();
        var store = new InMemoryCredentialStore();
        using var amkRing = new AmkWorkspaceKeyRing(amk);
        using var wkRing = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);

        CredentialVault pessoal = VaultOver(store, amkRing);
        SecretEnvelope envelope = await pessoal.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "cred-01", ActorUserId = "dono" },
            "senha-pessoal".AsMemory());

        CredentialVault doTime = VaultOver(store, wkRing);
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => doTime.RetrieveAsync(envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "dono" }));
    }

    /// <summary>
    /// <b>O achado da revisão da v1.4.7.</b> No esquema novo o <c>CredentialId</c> entra no AAD:
    /// mover o envelope para outra credencial (o que um servidor malicioso faria alterando o
    /// cabeçalho <c>type|credentialId</c>, que hoje viaja fora de qualquer AAD) quebra o tag GCM.
    /// </summary>
    [Fact]
    public async Task Wk_CredentialIdTrocado_NaoAbre()
    {
        var store = new InMemoryCredentialStore();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());
        CredentialVault vault = VaultOver(store, ring);

        SecretEnvelope envelope = await vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "equipamento-X", ActorUserId = "dono" },
            "senha-do-X".AsMemory());

        await store.SaveAsync(envelope with { CredentialId = "equipamento-Y" });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => vault.RetrieveAsync(envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "colega" }));
    }

    /// <summary>
    /// O <c>Algorithm</c> também entra no AAD do esquema novo: um servidor que rebaixasse o carimbo
    /// para <c>AmkRootedV1</c> (na esperança de que o cliente montasse o AAD antigo, sem
    /// credentialId) não consegue — o tag não fecha nas duas leituras.
    /// </summary>
    [Fact]
    public async Task Wk_AlgorithmRebaixado_NaoAbre()
    {
        var store = new InMemoryCredentialStore();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());
        CredentialVault vault = VaultOver(store, ring);

        SecretEnvelope envelope = await vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "cred-01", ActorUserId = "dono" },
            "senha".AsMemory());

        await store.SaveAsync(envelope with { Algorithm = VaultAlgorithms.AmkRootedV1 });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => vault.RetrieveAsync(envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "dono" }));
    }

    /// <summary>
    /// <b>Guarda de regressão do que NÃO pode mudar.</b> O AAD do <c>AmkRootedV1</c> continua sendo
    /// <c>env|id|ws|vN|type</c> — sem credentialId. Trocar o credentialId de um envelope já selado
    /// segue abrindo, e é por isso que o campo entra só no esquema novo: mexer no AAD antigo
    /// tornaria ilegível TUDO o que já está selado em produção. A limitação é conhecida e assumida.
    /// </summary>
    [Fact]
    public async Task Amk_CredentialIdTrocado_ContinuaAbrindo_LimitacaoAssumidaDoEsquemaAntigo()
    {
        var store = new InMemoryCredentialStore();
        using var ring = new AmkWorkspaceKeyRing(Amk());
        CredentialVault vault = VaultOver(store, ring);

        SecretEnvelope envelope = await vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "equipamento-X", ActorUserId = "dono" },
            "senha-do-X".AsMemory());

        await store.SaveAsync(envelope with { CredentialId = "equipamento-Y" });

        using VaultSecret aberto = await vault.RetrieveAsync(
            envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "dono" });
        Assert.Equal("senha-do-X", aberto.RevealString());
    }

    /// <summary>
    /// O que o esquema antigo JÁ protegia continua protegido no novo: adulterar o <c>Type</c> ou a
    /// <c>Version</c> quebra o tag. O AAD novo ADICIONA campos, não troca os que já havia.
    /// </summary>
    [Theory]
    [InlineData("type")]
    [InlineData("version")]
    public async Task Wk_CamposDoAadAntigo_ContinuamProtegidos(string campo)
    {
        var store = new InMemoryCredentialStore();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());
        CredentialVault vault = VaultOver(store, ring);

        SecretEnvelope envelope = await vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = Workspace, CredentialId = "cred-01", Type = "password", ActorUserId = "dono" },
            "senha".AsMemory());

        await store.SaveAsync(campo == "type"
            ? envelope with { Type = "privateKey" }
            : envelope with { Version = envelope.Version + 1 });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => vault.RetrieveAsync(envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "dono" }));
    }
}
