using System.Linq;
using System.Text;

using RemoteOps.Security.Account;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// <b>A razão de existir da Fase 1</b> (spec §1/§6/§12): "logo numa conta em qualquer PC e as senhas
/// dos equipamentos aparecem — e decifram".
///
/// <para>O teste encena os dois devices de verdade: o A sela a senha do equipamento sob a WDK
/// derivada da AMK e empurra o envelope; o B começa com o cofre VAZIO, tem só a senha da CONTA (+ o
/// escrow que o servidor guarda), desembrulha a AMK e puxa. Se a senha do equipamento não voltar
/// byte-idêntica no B, a fase não fecha — não existe asserção mais importante nesta frente.</para>
///
/// <para>Nada aqui é atalho: o cofre é o <c>CredentialVault</c> real, a cripto é a real
/// (Argon2id → KEK → AMK → HKDF → WDK → CEK → AES-256-GCM) e o servidor fake recusa o que o backend
/// real recusa. O único fake é a REDE.</para>
/// </summary>
public sealed class DeviceToDeviceSecretSyncTests
{
    private const string AccountPassword = "senha-da-conta-do-operador-2026";
    private const string HostPassword = "s3nh@-do-NE8000-çÃo-#!$%";
    private static readonly string ServerWorkspaceId = Guid.NewGuid().ToString();

    /// <summary>
    /// O fluxo inteiro do §6, ponta a ponta. Device B nasce vazio e termina abrindo a senha do
    /// equipamento que o device A digitou — sem nunca ter visto a AMK em claro pela rede.
    /// </summary>
    [Fact]
    public async Task DeviceB_ComCofreVazio_E_SoASenhaDaConta_AbreOSegredoSeladoNoDeviceA()
    {
        var keys = new AccountKeyService();
        var api = new FakeSecretsApi();
        // Se ISSO aparecer em qualquer campo do fio, o E2EE está quebrado.
        api.Forbid(HostPassword);
        api.Forbid(AccountPassword);

        // ── Device A: cria a conta, sela a senha do equipamento e sobe o envelope ──────────
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);
        string credentialId = Guid.NewGuid().ToString("n");
        string envelopeId;

        using (var deviceA = new SecretSyncDevice("A", enrollment.Amk, api, ServerWorkspaceId))
        {
            SecretEnvelope sealedEnvelope = await deviceA.SealAsync(credentialId, HostPassword);
            envelopeId = sealedEnvelope.EnvelopeId;

            await deviceA.Secrets.SyncOnceAsync();
            Assert.Single(api.Accepted);
        }

        // ── Device B: outro PC. Tem a SENHA da conta e o escrow — nada mais. ───────────────
        // É exatamente o §6: GET /auth/kdf devolve salt/params, a senha deriva a KEK, a KEK
        // desembrulha a AMK que o servidor guardava cifrada. A AMK nunca trafegou em claro.
        byte[] amkOnDeviceB = keys.UnwrapAmkWithPassword(
            AccountPassword, enrollment.Argon2Salt, enrollment.Params, enrollment.WrappedAmkPwd);

        using var deviceB = new SecretSyncDevice("B", amkOnDeviceB, api, ServerWorkspaceId);

        // O cofre do B começa vazio DE VERDADE — senão o teste provaria nada.
        Assert.Empty(await deviceB.ListAsync());

        await deviceB.Secrets.SyncOnceAsync();

        // O envelope chegou...
        SecretEnvelope received = Assert.Single(await deviceB.ListAsync());
        Assert.Equal(envelopeId, received.EnvelopeId);
        Assert.Equal(credentialId, received.CredentialId);
        Assert.Equal("password", received.Type);
        Assert.Equal(SecretSyncDevice.VaultWorkspaceId, received.WorkspaceId);

        // ...e ABRE, byte-idêntico. Este é o objetivo da fase.
        using VaultSecret opened = await deviceB.OpenAsync(envelopeId);
        Assert.Equal(Encoding.UTF8.GetBytes(HostPassword), opened.RevealUtf8().ToArray());
        Assert.Equal(HostPassword, opened.RevealString());
    }

    /// <summary>
    /// Vários segredos, incluindo os tipos que o changelog NÃO sabe descrever (a passphrase de chave
    /// privada mora em <c>metadata_json</c>, que não sincroniza). Se o transporte dependesse do
    /// changelog pra recuperar o <c>Type</c>, este caso quebraria — e o Type entra no AAD do GCM.
    /// </summary>
    [Fact]
    public async Task VariosSegredosDeTiposDiferentes_ChegamEAbremNoDeviceB()
    {
        var keys = new AccountKeyService();
        var api = new FakeSecretsApi();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        (string Cred, string Secret, string Type)[] data =
        [
            ("cred-senha", "s3nh@-do-switch", "password"),
            ("cred-chave", "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END-----", "privateKey"),
            ("cred-pass", "passphrase-da-chave-çãó", "privateKeyPassphrase"),
            ("cred-longo", new string('x', 512), "password"),
        ];

        foreach ((_, string secret, _) in data)
        {
            api.Forbid(secret);
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var deviceA = new SecretSyncDevice("A", enrollment.Amk, api, ServerWorkspaceId))
        {
            foreach ((string cred, string secret, string type) in data)
            {
                ids[cred] = (await deviceA.SealAsync(cred, secret, type)).EnvelopeId;
            }

            await deviceA.Secrets.SyncOnceAsync();
        }

        byte[] amkOnDeviceB = keys.UnwrapAmkWithPassword(
            AccountPassword, enrollment.Argon2Salt, enrollment.Params, enrollment.WrappedAmkPwd);
        using var deviceB = new SecretSyncDevice("B", amkOnDeviceB, api, ServerWorkspaceId);
        await deviceB.Secrets.SyncOnceAsync();

        Assert.Equal(data.Length, (await deviceB.ListAsync()).Count);
        foreach ((string cred, string secret, string type) in data)
        {
            using VaultSecret opened = await deviceB.OpenAsync(ids[cred]);
            Assert.Equal(secret, opened.RevealString());

            SecretEnvelope env = Assert.Single(await deviceB.ListAsync(), e => e.EnvelopeId == ids[cred]);
            Assert.Equal(type, env.Type);
        }
    }

    /// <summary>
    /// A senha errada não abre nada — e a prova tem que ser no CAMINHO REAL: o device C desembrulha
    /// a AMK com a senha errada (falha na hora), e mesmo forçando uma AMK aleatória o envelope
    /// baixado não abre. Confirma que o segredo depende da senha, não de estar na rede.
    /// </summary>
    [Fact]
    public async Task SenhaErrada_NaoAbreOCofre_MesmoComOEnvelopeBaixado()
    {
        var keys = new AccountKeyService();
        var api = new FakeSecretsApi();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        string envelopeId;
        using (var deviceA = new SecretSyncDevice("A", enrollment.Amk, api, ServerWorkspaceId))
        {
            envelopeId = (await deviceA.SealAsync("cred-1", HostPassword)).EnvelopeId;
            await deviceA.Secrets.SyncOnceAsync();
        }

        // Senha errada: nem chega a ter AMK.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            keys.UnwrapAmkWithPassword(
                "senha-errada", enrollment.Argon2Salt, enrollment.Params, enrollment.WrappedAmkPwd));

        // E com uma AMK qualquer, o envelope baixado continua fechado (tag GCM falha).
        byte[] amkIntruso = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var intruso = new SecretSyncDevice("X", amkIntruso, api, ServerWorkspaceId);
        await intruso.Secrets.SyncOnceAsync();

        Assert.Single(await intruso.ListAsync()); // o blob chega...
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => intruso.OpenAsync(envelopeId)); // ...mas não abre.
    }

    /// <summary>
    /// Workspace errado não vaza: o RBAC de verdade é do servidor, mas o cliente também não pode
    /// MISTURAR workspaces. Um device apontado pra outro workspace do servidor não enxerga os
    /// envelopes deste — o pull é filtrado por workspace na origem.
    /// </summary>
    [Fact]
    public async Task DeviceDeOutroWorkspace_NaoRecebeOsEnvelopes()
    {
        var keys = new AccountKeyService();
        var api = new FakeSecretsApi();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        using (var deviceA = new SecretSyncDevice("A", enrollment.Amk, api, ServerWorkspaceId))
        {
            await deviceA.SealAsync("cred-1", HostPassword);
            await deviceA.Secrets.SyncOnceAsync();
        }

        // Mesma conta/AMK, OUTRO workspace do servidor: o cofre continua vazio.
        string outroWorkspace = Guid.NewGuid().ToString();
        using var deviceOutro = new SecretSyncDevice("Z", enrollment.Amk, api, outroWorkspace);
        await deviceOutro.Secrets.SyncOnceAsync();

        Assert.Empty(await deviceOutro.ListAsync());
    }
}
