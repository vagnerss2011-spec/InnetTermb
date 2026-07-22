using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// O ativador de produção, com o cofre de VERDADE (FileVaultStore + CredentialVault +
/// LocalVaultMigrator) — só o DPAPI é trocado por um protetor fake, pra o teste não depender do
/// usuário do Windows.
///
/// O que isto pega e um teste com fakes não pegaria: que os segredos selados sob a raiz DPAPI ANTIGA
/// continuam legíveis depois que o app troca pra raiz AMK. É o cenário do operador que já tem hosts
/// e senhas e faz login numa conta pela primeira vez (spec §7) — se quebrar, ele perde o cofre.
/// </summary>
public sealed class VaultRootActivatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "remoteops-activator-tests", Guid.NewGuid().ToString("N"));

    public VaultRootActivatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* limpeza best-effort */ }
    }

    /// <summary>Protetor determinístico: substitui o DPAPI (que exige o usuário real do Windows).</summary>
    private sealed class FakeProtector : ILocalKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
            => Xor(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
            => Xor(protectedBlob, entropy);

        private static byte[] Xor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        {
            byte[] key = SHA256.HashData(entropy);
            byte[] result = data.ToArray();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= key[i % key.Length];
            }

            return result;
        }
    }

    private string VaultPath => Path.Combine(_dir, "vault.json");

    private static byte[] SampleAmk() => [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 3))];

    private (FileVaultStore Store, WorkspaceKeyRing Legacy) NewLegacyStack()
    {
        var store = new FileVaultStore(VaultPath);
        return (store, new WorkspaceKeyRing(store, new FakeProtector()));
    }

    /// <summary>
    /// A PROVA da Fase 1 no cofre real: um segredo selado sob a raiz DPAPI antiga continua abrindo
    /// depois que o app passa pra raiz AMK — nos DOIS workspaces que o app usa.
    /// </summary>
    [Fact]
    public async Task Activate_KeepsLegacySecretsReadableUnderTheAmkRoot()
    {
        // Cofre "de antes": segredos selados sob a raiz DPAPI, nos dois workspaces reais.
        string credentialEnvelopeId;
        string dbKeyEnvelopeId;
        (FileVaultStore legacyStore, WorkspaceKeyRing legacyRing) = NewLegacyStack();
        {
            ICredentialVault legacyVault = new CredentialVault(
                legacyStore, legacyRing, new InMemoryVaultAuditSink());
            credentialEnvelopeId = await legacyVault.StoreSecretAsync(
                "senha-do-roteador", AppRuntimeMirror.CredentialsWorkspace);
            dbKeyEnvelopeId = await legacyVault.StoreSecretAsync(
                "deadbeefcafe", AppRuntimeMirror.DbWorkspace);
        }

        // O operador loga numa conta: a raiz vira AMK.
        var store = new FileVaultStore(VaultPath);
        var legacy = new WorkspaceKeyRing(store, new FakeProtector());
        using var activator = new VaultRootActivator(store, legacy, Path.Combine(_dir, "t.tokenref"));

        await activator.ActivateAsync(
            SampleAmk(), "ws-server", AppRuntimeMirror.VaultWorkspaces);

        // Os dois segredos abrem sob a raiz NOVA, com o mesmo conteúdo.
        ICredentialVault amkVault = activator.Vault!;
        Assert.Equal("senha-do-roteador", await amkVault.RetrieveSecretAsync(credentialEnvelopeId));
        Assert.Equal("deadbeefcafe", await amkVault.RetrieveSecretAsync(dbKeyEnvelopeId));
    }

    /// <summary>
    /// O workspace "local" guarda a CHAVE DO BANCO SQLCipher. Se ele ficasse de fora da migração, o
    /// app abriria o cofre e não abriria o banco — e o operador veria só "Não foi possível iniciar".
    /// Este teste falha se alguém tirar DbWorkspace do AppRuntime.VaultWorkspaces.
    /// </summary>
    [Fact]
    public async Task Activate_MigratesTheDbKeyWorkspace_OrTheAppWouldNotStart()
    {
        string dbKeyEnvelopeId;
        {
            var seedStore = new FileVaultStore(VaultPath);
            ICredentialVault legacyVault = new CredentialVault(
                seedStore,
                new WorkspaceKeyRing(seedStore, new FakeProtector()),
                new InMemoryVaultAuditSink());
            dbKeyEnvelopeId = await legacyVault.StoreSecretAsync("chave-do-banco", AppRuntimeMirror.DbWorkspace);
        }

        var store = new FileVaultStore(VaultPath);
        using var activator = new VaultRootActivator(
            store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));

        // Migra SÓ as credenciais — o esquecimento que a T6 encontrou.
        await activator.ActivateAsync(SampleAmk(), "ws-server", [AppRuntimeMirror.CredentialsWorkspace]);

        // A chave do banco continua selada sob a raiz antiga → o cofre AMK não a abre. É exatamente
        // a exceção que derrubaria o startup.
        ICredentialVault amkVault = activator.Vault!;
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => amkVault.RetrieveSecretAsync(dbKeyEnvelopeId));
    }

    /// <summary>Ativar 2x é no-op na migração (idempotente) e o cofre segue legível.</summary>
    [Fact]
    public async Task Activate_Twice_IsIdempotent()
    {
        string envelopeId;
        {
            var seedStore = new FileVaultStore(VaultPath);
            ICredentialVault legacyVault = new CredentialVault(
                seedStore,
                new WorkspaceKeyRing(seedStore, new FakeProtector()),
                new InMemoryVaultAuditSink());
            envelopeId = await legacyVault.StoreSecretAsync("segredo", AppRuntimeMirror.CredentialsWorkspace);
        }

        for (int i = 0; i < 2; i++)
        {
            var store = new FileVaultStore(VaultPath);
            using var activator = new VaultRootActivator(
                store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));
            await activator.ActivateAsync(SampleAmk(), "ws-server", AppRuntimeMirror.VaultWorkspaces);
            ICredentialVault amkVault = activator.Vault!;
            Assert.Equal("segredo", await amkVault.RetrieveSecretAsync(envelopeId));
        }
    }

    /// <summary>
    /// Cofre novo (instalação limpa): ativar não estoura e o cofre já nasce AMK-rooted — este é o
    /// caminho de quem cria a conta no primeiro uso.
    /// </summary>
    [Fact]
    public async Task Activate_OnEmptyVault_Works()
    {
        var store = new FileVaultStore(VaultPath);
        using var activator = new VaultRootActivator(
            store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));

        await activator.ActivateAsync(SampleAmk(), "ws-server", AppRuntimeMirror.VaultWorkspaces);

        ICredentialVault amkVault = activator.Vault!;
        string id = await amkVault.StoreSecretAsync("nova", AppRuntimeMirror.CredentialsWorkspace);
        Assert.Equal("nova", await amkVault.RetrieveSecretAsync(id));
    }

    /// <summary>
    /// Os tokens são escopados pelo workspace do SERVIDOR e gravados no cofre JÁ AMK-rooted — é
    /// isso que faz o próximo boot conseguir lê-los sob a mesma raiz.
    /// </summary>
    [Fact]
    public async Task Activate_ReturnsTokenStore_BoundToTheActivatedVault()
    {
        var store = new FileVaultStore(VaultPath);
        using var activator = new VaultRootActivator(
            store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));

        ITokenStore tokens = await activator.ActivateAsync(
            SampleAmk(), "ws-server", AppRuntimeMirror.VaultWorkspaces);
        await tokens.SaveAsync(new TokenSet("acc", "ref", DateTimeOffset.UtcNow.AddHours(1)));

        TokenSet? loaded = await tokens.LoadAsync();
        Assert.Equal("acc", loaded!.AccessToken);
        Assert.Equal("ref", loaded.RefreshToken);
    }

    // ── Cofre de TIME (Fatia 1) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠️ O cofre do TIME entra na lista efetiva da ativação (fora dela o app trava na abertura),
    /// mas NÃO pode ser migrado: a migração o carimbaria como "derivado da conta", e a chave dele é
    /// o oposto — aleatória e COMPARTILHADA. O carimbo errado não daria erro nenhum agora; só faria
    /// o cofre do time abrir com a chave errada depois.
    /// </summary>
    [Fact]
    public async Task Activate_NaoMigraOCofreDoTime_EOMantemNaListaEfetiva()
    {
        var store = new FileVaultStore(VaultPath);
        using var activator = new VaultRootActivator(
            store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));

        const string serverWorkspace = "8f3b6f4a-0000-4000-8000-000000000001";
        await activator.ActivateAsync(SampleAmk(), serverWorkspace, AppRuntimeMirror.VaultWorkspaces);

        string teamVault = RemoteOps.Desktop.Account.AppRuntime.TeamVaultWorkspace(serverWorkspace);

        // Está na lista efetiva (é o ativador que a monta) …
        Assert.Contains(
            teamVault,
            RemoteOps.Desktop.Account.AppRuntime.VaultWorkspacesFor(
                serverWorkspace, AppRuntimeMirror.VaultWorkspaces));

        // … e mesmo assim NÃO foi carimbado como derivado da AMK.
        Assert.NotEqual(VaultKeyRooting.AmkDerived, await store.LoadKeyRootingAsync(teamVault));

        // Os cofres de sempre continuam migrados — a guarda do time não desligou o resto.
        Assert.Equal(
            VaultKeyRooting.AmkDerived,
            await store.LoadKeyRootingAsync(AppRuntimeMirror.CredentialsWorkspace));
        Assert.Equal(
            VaultKeyRooting.AmkDerived,
            await store.LoadKeyRootingAsync(AppRuntimeMirror.DbWorkspace));
    }

    /// <summary>
    /// A ativação publica o chaveiro do TIME (é dele que o convite sai e por onde a chave entra). Sem
    /// ele, a tela de Equipe não teria como embrulhar a WK sob a AMK — e o convite nasceria morto.
    /// </summary>
    [Fact]
    public async Task Activate_PublicaOChaveiroDoTime()
    {
        var store = new FileVaultStore(VaultPath);
        using var activator = new VaultRootActivator(
            store, new WorkspaceKeyRing(store, new FakeProtector()), Path.Combine(_dir, "t.tokenref"));

        Assert.Null(activator.TeamKeyRing); // antes da AMK não há como embrulhar nada

        await activator.ActivateAsync(SampleAmk(), "ws-server", AppRuntimeMirror.VaultWorkspaces);

        Assert.NotNull(activator.TeamKeyRing);
        Assert.Equal(VaultAlgorithms.WkRootedV1, activator.TeamKeyRing.AlgorithmId);
    }

    /// <summary>
    /// Espelho de <c>AppRuntime</c> (que é internal). Se estes valores divergirem dos reais, os
    /// testes acima param de provar o que dizem provar — por isso o assert de consistência abaixo.
    /// </summary>
    private static class AppRuntimeMirror
    {
        internal const string CredentialsWorkspace = "ws-local";
        internal const string DbWorkspace = "local";
        internal static readonly string[] VaultWorkspaces = [CredentialsWorkspace, DbWorkspace];
    }

    /// <summary>
    /// Amarra o espelho ao real: o workspace do banco tem que ser o mesmo que o App passa pro
    /// OpenWorkspaceAsync, e o das credenciais o mesmo que os ViewModels usam.
    /// </summary>
    [Fact]
    public void Mirror_MatchesTheWorkspacesTheAppActuallyUses()
    {
        Assert.Equal(
            RemoteOps.Desktop.ViewModels.WorkspaceViewModel.WorkspaceId,
            AppRuntimeMirror.CredentialsWorkspace);
    }
}
