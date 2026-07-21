using System.Linq;
using System.Security.Cryptography;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Cloud;

using Xunit;

namespace RemoteOps.UnitTests.Integration;

/// <summary>
/// <b>Os dois PCs do operador contra o servidor de verdade.</b>
///
/// <para>Esta suíte existe porque a camada que quebrou em produção era exatamente a que nenhum teste
/// tocava. Os testes de dois devices que já existiam (<c>DeviceToDeviceSecretSyncTests</c>,
/// <c>DeviceToDeviceWorkspaceSyncTests</c>) trocam o transporte por <c>FakeChangelogApi</c>/
/// <c>FakeSecretsApi</c>, e o <c>SecretsTransportTests</c> chama o serviço do backend direto: entre um
/// e outro ficava um vão do tamanho da rota, do binding do JSON, do Bearer, do <c>X-Device-Id</c> e do
/// RBAC — e foi nesse vão que o operador ficou com os hosts sincronizados e as credenciais não.</para>
///
/// <para>Aqui não há dublê de rede: o <see cref="CloudApiFactory"/> hospeda o <c>Program.cs</c>
/// completo e os clientes são os de produção. Os dois cofres são reais e compartilham a AMK, então a
/// asserção nunca é "chegou um blob parecido": é <b>o device B ABRE o segredo</b>.</para>
/// </summary>
public sealed class TwoDeviceCloudSyncTests
{
    private const string AccountPassword = "senha-da-conta-do-operador-2026";
    private const string KeychainPassword = "s3nh@-do-NE8000-çÃo-#!$%";
    private const string InlinePassword = "senha-inline-do-switch-#42";
    private const string RotatedPassword = "s3nh@-nova-depois-da-troca-2026";
    private const string Username = "operador-noc";

    /// <summary>
    /// <b>(a) Round-trip.</b> O PC A cadastra host com credencial de CHAVEIRO e host com senha
    /// INLINE; o PC B nasce vazio, entra na mesma conta por <c>/auth/login</c> e tem que terminar com
    /// os hosts, os endereços, os VÍNCULOS host→credencial e — a asserção que importa — as duas
    /// senhas ABRINDO byte a byte no cofre dele.
    ///
    /// <para>A decifração real é o que valida o caminho inteiro: o AAD do AES-GCM é montado de
    /// <c>envelopeId|workspaceId|version|type</c>, então qualquer campo que o round-trip HTTP altere
    /// (o formato do GUID que o servidor devolve, o cabeçalho que viaja no <c>keyVersion</c>) faz o
    /// segredo não abrir. Um teste que só comparasse contagens não veria nada disso.</para>
    /// </summary>
    [Fact]
    public async Task DeviceB_PelaApiReal_VeOsHostsEABREAsSenhasDeChaveiroEInline()
    {
        using var factory = new CloudApiFactory();
        CloudAccountFixture account = await CloudAccountFixture.EnrollAsync(factory, AccountPassword);

        string groupId;
        string keychainAssetId;
        string keychainEndpointId;
        string keychainCredId = Guid.NewGuid().ToString("n");
        string keychainEnvelopeId;
        string inlineAssetId;
        string inlineEndpointId;
        string inlineCredId = Guid.NewGuid().ToString("n");
        string inlineEnvelopeId;

        // ── PC A: onde o operador cadastrou tudo ──────────────────────────────────────────
        await using (CloudSyncedDevice deviceA = await CloudSyncedDevice.CreateAsync(
            "A", factory, account.Amk, account.ServerWorkspaceId,
            account.PrimaryTokens, account.PrimaryDeviceId))
        {
            AssetGroup group = await deviceA.Store.AddGroupAsync(
                CloudSyncedDevice.VaultWorkspaceId, "Backbone");
            groupId = group.Id;

            Asset keychainAsset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = CloudSyncedDevice.VaultWorkspaceId,
                GroupId = groupId,
                Name = "NE8000-CORE-01",
                Vendor = "Huawei",
                Model = "NE8000 M8",
                DeviceRole = DeviceRoles.Router,
                Site = "POP Centro",
                Tags = ["backbone", "critico"],
            });
            keychainAssetId = keychainAsset.Id;

            SecretEnvelope keychainEnvelope = await deviceA.SealAsync(keychainCredId, KeychainPassword);
            keychainEnvelopeId = keychainEnvelope.EnvelopeId;

            // Credencial de CHAVEIRO: Scope nulo — aparece na lista do Chaveiro e serve a vários hosts.
            await deviceA.Store.AddCredentialRefAsync(new CredentialRef
            {
                Id = keychainCredId,
                Name = "NOC - acesso backbone",
                Type = CredentialTypes.Password,
                Metadata = new CredentialMetadata { Username = Username },
                SecretEnvelopeId = keychainEnvelopeId,
            });

            keychainEndpointId = Guid.NewGuid().ToString("n");
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = keychainEndpointId,
                AssetId = keychainAssetId,
                Protocol = "ssh",
                Fqdn = "ne8000-core-01.rede.local",
                Ipv4 = "10.20.30.40",
                Port = 2222,
                CredentialRefId = keychainCredId,
                Profile = new EndpointProfile { BackspaceMode = TerminalBackspaceModes.ControlH },
            });

            Asset inlineAsset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = CloudSyncedDevice.VaultWorkspaceId,
                Name = "SW-ACESSO-07",
                Vendor = "MikroTik",
                DeviceRole = DeviceRoles.Switch,
            });
            inlineAssetId = inlineAsset.Id;

            SecretEnvelope inlineEnvelope = await deviceA.SealAsync(inlineCredId, InlinePassword);
            inlineEnvelopeId = inlineEnvelope.EnvelopeId;

            // Senha INLINE do device: escondida do Chaveiro pelo Scope "endpoint:<id>". Os dois tipos
            // têm que atravessar — o operador relatou o problema nos dois.
            await deviceA.Store.AddCredentialRefAsync(new CredentialRef
            {
                Id = inlineCredId,
                Name = "(senha do dispositivo)",
                Type = CredentialTypes.Password,
                Scope = $"endpoint:{inlineAssetId}",
                Metadata = new CredentialMetadata { Username = "admin" },
                SecretEnvelopeId = inlineEnvelopeId,
            });

            inlineEndpointId = Guid.NewGuid().ToString("n");
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = inlineEndpointId,
                AssetId = inlineAssetId,
                Protocol = "ssh",
                Ipv4 = "192.168.1.7",
                Port = 22,
                CredentialRefId = inlineCredId,
            });

            await deviceA.SyncOnceAsync();
        }

        // ── PC B: outro computador, mesma conta. Login REAL, device novo. ─────────────────
        (TokenSet tokensB, Guid deviceIdB) =
            await account.LoginNewDeviceAsync(factory, "PC do operador (B)");

        await using CloudSyncedDevice deviceB = await CloudSyncedDevice.CreateAsync(
            "B", factory, account.Amk, account.ServerWorkspaceId, tokensB, deviceIdB);

        // Vazio DE VERDADE antes do ciclo — senão o teste não provaria nada.
        Assert.Empty(await deviceB.Store.GetAssetsAsync(CloudSyncedDevice.VaultWorkspaceId));
        Assert.Empty(await deviceB.ListEnvelopesAsync());

        await deviceB.SyncOnceAsync();

        // O canal de segredos tem que ter rodado LIMPO: nada pulado, nada em silêncio.
        Assert.Equal(SecretChannelState.Healthy, deviceB.Sync.Status.SecretChannel);

        AssetGroup receivedGroup = Assert.Single(
            await deviceB.Store.GetGroupsAsync(CloudSyncedDevice.VaultWorkspaceId));
        Assert.Equal(groupId, receivedGroup.Id);
        Assert.Equal("Backbone", receivedGroup.Name);

        IReadOnlyList<Asset> assets = await deviceB.Store.GetAssetsAsync(CloudSyncedDevice.VaultWorkspaceId);
        Assert.Equal(2, assets.Count);

        // ── Host 1: credencial de chaveiro ────────────────────────────────────────────────
        Asset receivedKeychainAsset = Assert.Single(assets, a => a.Id == keychainAssetId);
        Assert.Equal("NE8000-CORE-01", receivedKeychainAsset.Name);
        Assert.Equal(groupId, receivedKeychainAsset.GroupId);
        Assert.Equal(DeviceRoles.Router, receivedKeychainAsset.DeviceRole);
        Assert.Equal(["backbone", "critico"], receivedKeychainAsset.Tags);

        Endpoint receivedKeychainEndpoint = Assert.Single(receivedKeychainAsset.Endpoints);
        Assert.Equal(keychainEndpointId, receivedKeychainEndpoint.Id);
        Assert.Equal("ne8000-core-01.rede.local", receivedKeychainEndpoint.Fqdn);
        Assert.Equal("10.20.30.40", receivedKeychainEndpoint.Ipv4);
        Assert.Equal(2222, receivedKeychainEndpoint.Port);
        Assert.Equal(TerminalBackspaceModes.ControlH, receivedKeychainEndpoint.Profile?.BackspaceMode);
        // O VÍNCULO — sem ele o app diz "o endpoint não tem credencial".
        Assert.Equal(keychainCredId, receivedKeychainEndpoint.CredentialRefId);

        CredentialRef? receivedKeychainCred = await deviceB.Store.GetCredentialRefAsync(keychainCredId);
        Assert.NotNull(receivedKeychainCred);
        Assert.Equal("NOC - acesso backbone", receivedKeychainCred.Name);
        Assert.Equal(Username, receivedKeychainCred.Metadata?.Username);
        Assert.Equal(keychainEnvelopeId, receivedKeychainCred.SecretEnvelopeId);

        // ── Host 2: senha inline ──────────────────────────────────────────────────────────
        Asset receivedInlineAsset = Assert.Single(assets, a => a.Id == inlineAssetId);
        Endpoint receivedInlineEndpoint = Assert.Single(receivedInlineAsset.Endpoints);
        Assert.Equal(inlineEndpointId, receivedInlineEndpoint.Id);
        Assert.Equal(inlineCredId, receivedInlineEndpoint.CredentialRefId);

        CredentialRef? receivedInlineCred = await deviceB.Store.GetCredentialRefAsync(inlineCredId);
        Assert.NotNull(receivedInlineCred);
        Assert.Equal($"endpoint:{inlineAssetId}", receivedInlineCred.Scope);
        Assert.Equal(inlineEnvelopeId, receivedInlineCred.SecretEnvelopeId);

        // ── E as DUAS senhas abrem, byte-idênticas. É o objetivo da frente. ───────────────
        Assert.Equal(2, (await deviceB.ListEnvelopesAsync()).Count);

        using (VaultSecret opened = await deviceB.OpenAsync(receivedKeychainCred.SecretEnvelopeId!))
        {
            Assert.Equal(KeychainPassword, opened.RevealString());
        }

        using (VaultSecret opened = await deviceB.OpenAsync(receivedInlineCred.SecretEnvelopeId!))
        {
            Assert.Equal(InlinePassword, opened.RevealString());
        }
    }

    /// <summary>
    /// <b>(b) O reparo dos ~700.</b> Reproduz o incidente pela raiz: a fila do PC A leva o patch do
    /// endpoint SEM <c>credential_ref_id</c> (o patch congelado da versão antiga), então o servidor
    /// guarda o vínculo nulo e o PC B recebe o host sem credencial — o "o endpoint não tem credencial"
    /// relatado. Depois o operador clica em "Reenviar tudo para a nuvem" no A e o B passa a ver o
    /// vínculo.
    ///
    /// <para>O primeiro bloco de asserções (B com o vínculo NULO) não é decoração: sem ele o teste
    /// passaria mesmo que a reprodução não tivesse funcionado, e o reparo estaria consertando um
    /// problema que o teste nunca criou.</para>
    /// </summary>
    [Fact]
    public async Task ReenviarTudo_ReparaOEndpointQueSubiuSemCredencial()
    {
        using var factory = new CloudApiFactory();
        CloudAccountFixture account = await CloudAccountFixture.EnrollAsync(factory, AccountPassword);

        await using CloudSyncedDevice deviceA = await CloudSyncedDevice.CreateAsync(
            "A", factory, account.Amk, account.ServerWorkspaceId,
            account.PrimaryTokens, account.PrimaryDeviceId);

        (TokenSet tokensB, Guid deviceIdB) =
            await account.LoginNewDeviceAsync(factory, "PC do operador (B)");
        await using CloudSyncedDevice deviceB = await CloudSyncedDevice.CreateAsync(
            "B", factory, account.Amk, account.ServerWorkspaceId, tokensB, deviceIdB);

        string credId = Guid.NewGuid().ToString("n");
        Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = CloudSyncedDevice.VaultWorkspaceId,
            Name = "OLT-POP-SUL-03",
            Vendor = "Huawei",
            DeviceRole = DeviceRoles.Olt,
        });

        SecretEnvelope envelope = await deviceA.SealAsync(credId, KeychainPassword);
        await deviceA.Store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = "OLT - acesso",
            Type = CredentialTypes.Password,
            Metadata = new CredentialMetadata { Username = Username },
            SecretEnvelopeId = envelope.EnvelopeId,
        });

        string endpointId = Guid.NewGuid().ToString("n");
        await deviceA.Store.AddEndpointAsync(new Endpoint
        {
            Id = endpointId,
            AssetId = asset.Id,
            Protocol = "ssh",
            Ipv4 = "10.9.8.7",
            Port = 22,
            CredentialRefId = credId,
        });

        // O defeito: a linha da FILA sobe sem o vínculo. O banco local do A segue completo.
        Assert.Equal(1, await deviceA.DropFieldFromQueuedPatchAsync("endpoint", endpointId, "credential_ref_id"));
        Endpoint? localOnA = await deviceA.Store.GetEndpointAsync(endpointId);
        Assert.Equal(credId, localOnA?.CredentialRefId);

        await deviceA.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        // ── O sintoma de produção, reproduzido ────────────────────────────────────────────
        // O Chaveiro do B LISTA a credencial (o metadado subiu inteiro)...
        CredentialRef? credOnB = await deviceB.Store.GetCredentialRefAsync(credId);
        Assert.NotNull(credOnB);
        Assert.Equal("OLT - acesso", credOnB.Name);
        // ...e o host está lá, com endereço. Só o VÍNCULO chegou nulo.
        Endpoint? brokenOnB = await deviceB.Store.GetEndpointAsync(endpointId);
        Assert.NotNull(brokenOnB);
        Assert.Equal("10.9.8.7", brokenOnB.Ipv4);
        Assert.Null(brokenOnB.CredentialRefId);

        // ── "Reenviar tudo para a nuvem" no PC A ──────────────────────────────────────────
        var resync = new CloudResyncService(
            deviceA.Store, CloudSyncedDevice.VaultWorkspaceId, new OrchestratorSyncController(deviceA.Sync));
        ResyncResult result = await resync.ResyncAllAsync();

        Assert.True(result.Ran);
        Assert.Equal(0, result.Failed);
        // 1 ativo + 1 endpoint + 1 credencial (sem grupos neste cenário).
        Assert.Equal(3, result.ReEmitted);

        await deviceB.SyncOnceAsync();

        // ── O reparo chegou ───────────────────────────────────────────────────────────────
        Endpoint? repairedOnB = await deviceB.Store.GetEndpointAsync(endpointId);
        Assert.NotNull(repairedOnB);
        Assert.Equal(credId, repairedOnB.CredentialRefId);
        Assert.Equal("10.9.8.7", repairedOnB.Ipv4); // o reenvio não pode ter zerado o resto

        // E a senha continua abrindo: o reparo mexeu em metadado, nunca em envelope.
        CredentialRef? repairedCred = await deviceB.Store.GetCredentialRefAsync(credId);
        using VaultSecret opened = await deviceB.OpenAsync(repairedCred!.SecretEnvelopeId!);
        Assert.Equal(KeychainPassword, opened.RevealString());
    }

    /// <summary>
    /// <b>(c) Rotação.</b> O operador troca a senha da credencial no PC A. O envelope novo tem ID
    /// NOVO e o antigo vira tombstone, então a troca só chega ao outro PC se o
    /// <c>CredentialRef</c> for repontado — e é esse repontar que também emite o patch no outbox.
    ///
    /// <para>Duas asserções, e as duas já falharam em campo: o B abre a senha NOVA, e o A continua
    /// abrindo a dele (antes, o ref do A ficava apontando pro tombstone e conectar falhava no PRÓPRIO
    /// PC com "Envelope revogado").</para>
    /// </summary>
    [Fact]
    public async Task TrocaDeSenhaNoPcA_ChegaAbrindoNoPcB_E_OPcAContinuaAbrindo()
    {
        using var factory = new CloudApiFactory();
        CloudAccountFixture account = await CloudAccountFixture.EnrollAsync(factory, AccountPassword);

        await using CloudSyncedDevice deviceA = await CloudSyncedDevice.CreateAsync(
            "A", factory, account.Amk, account.ServerWorkspaceId,
            account.PrimaryTokens, account.PrimaryDeviceId);

        (TokenSet tokensB, Guid deviceIdB) =
            await account.LoginNewDeviceAsync(factory, "PC do operador (B)");
        await using CloudSyncedDevice deviceB = await CloudSyncedDevice.CreateAsync(
            "B", factory, account.Amk, account.ServerWorkspaceId, tokensB, deviceIdB);

        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope original = await deviceA.SealAsync(credId, KeychainPassword);
        await deviceA.Store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = "Acesso do POP",
            Type = CredentialTypes.Password,
            Metadata = new CredentialMetadata { Username = Username },
            SecretEnvelopeId = original.EnvelopeId,
        });

        await deviceA.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        CredentialRef? beforeOnB = await deviceB.Store.GetCredentialRefAsync(credId);
        Assert.Equal(original.EnvelopeId, beforeOnB?.SecretEnvelopeId);
        using (VaultSecret old = await deviceB.OpenAsync(original.EnvelopeId))
        {
            Assert.Equal(KeychainPassword, old.RevealString());
        }

        // ── A troca acontece pela VM real do Chaveiro, não por um atalho no cofre ─────────
        var keychain = new KeychainViewModel(deviceA.Store, deviceA.Vault, CloudSyncedDevice.VaultWorkspaceId);
        CredentialRef credOnA = (await deviceA.Store.GetCredentialRefAsync(credId))!;
        await keychain.ChangePasswordAsync(credOnA, RotatedPassword.ToCharArray());

        await deviceA.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        // ── O PC A continua abrindo (o ref não ficou no tombstone) ───────────────────────
        CredentialRef? afterOnA = await deviceA.Store.GetCredentialRefAsync(credId);
        Assert.NotNull(afterOnA);
        Assert.NotEqual(original.EnvelopeId, afterOnA.SecretEnvelopeId);
        using (VaultSecret onA = await deviceA.OpenAsync(afterOnA.SecretEnvelopeId!))
        {
            Assert.Equal(RotatedPassword, onA.RevealString());
        }

        // ── E o PC B abre a senha NOVA ───────────────────────────────────────────────────
        CredentialRef? afterOnB = await deviceB.Store.GetCredentialRefAsync(credId);
        Assert.NotNull(afterOnB);
        Assert.Equal(afterOnA.SecretEnvelopeId, afterOnB.SecretEnvelopeId);
        using VaultSecret onB = await deviceB.OpenAsync(afterOnB.SecretEnvelopeId!);
        Assert.Equal(RotatedPassword, onB.RevealString());
    }

    /// <summary>
    /// <b>(d) Veneno.</b> Um envelope que este app nunca produziria (cabeçalho fora do formato que o
    /// <c>SecretEnvelopeWireCodec</c> exige) é plantado no servidor pelo cliente HTTP REAL, como faria
    /// um cliente de outra versão. O PC B tem que entregar os DEMAIS assim mesmo — e dizer que o canal
    /// saiu degradado.
    ///
    /// <para><b>Por que o veneno vai primeiro:</b> o cursor do pull só avança depois da página
    /// inteira. Sem isolamento por item, o primeiro item ruim congelaria o cursor e o PC B nunca mais
    /// receberia senha nenhuma — falha silenciosa, com a barra de status dizendo "Sincronizado".</para>
    /// </summary>
    [Fact]
    public async Task EnvelopeVenenosoNoServidor_NaoImpedeOsDemais_E_OCanalReportaDegradado()
    {
        using var factory = new CloudApiFactory();
        CloudAccountFixture account = await CloudAccountFixture.EnrollAsync(factory, AccountPassword);

        await using CloudSyncedDevice deviceA = await CloudSyncedDevice.CreateAsync(
            "A", factory, account.Amk, account.ServerWorkspaceId,
            account.PrimaryTokens, account.PrimaryDeviceId);

        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope good = await deviceA.SealAsync(credId, KeychainPassword);
        await deviceA.Store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = "Acesso sadio",
            Type = CredentialTypes.Password,
            Metadata = new CredentialMetadata { Username = Username },
            SecretEnvelopeId = good.EnvelopeId,
        });

        // O veneno entra ANTES do sadio, então fica com o cursor MENOR.
        string poisonId = Guid.NewGuid().ToString();
        IReadOnlyList<SecretUpsertResult> planted = await deviceA.SecretsApi.PushAsync(
            account.ServerWorkspaceId,
            [new SecretEnvelopeDto(
                Id: poisonId,
                WorkspaceId: account.ServerWorkspaceId,
                Ciphertext: Base64(96),
                Nonce: Base64(12),
                Tag: Base64(16),
                WrappedCek: Base64(60),
                CekNonce: Base64(12),
                CekTag: Base64(16),
                // O servidor aceita (não interpreta o campo); o codec do cliente recusa, porque sem
                // o Type certo o AAD nunca fecharia e o envelope "estaria lá" sem nunca abrir.
                KeyVersion: "cabecalho-de-um-cliente-desconhecido",
                Version: 1)]);
        Assert.Equal("ok", Assert.Single(planted).Status);

        await deviceA.SyncOnceAsync();

        (TokenSet tokensB, Guid deviceIdB) =
            await account.LoginNewDeviceAsync(factory, "PC do operador (B)");
        await using CloudSyncedDevice deviceB = await CloudSyncedDevice.CreateAsync(
            "B", factory, account.Amk, account.ServerWorkspaceId, tokensB, deviceIdB);

        await deviceB.SyncOnceAsync();

        // ── Os metadados passaram; o canal de segredos tem VOZ própria ───────────────────
        Assert.Equal(SyncState.Synced, deviceB.Sync.Status.State);
        Assert.Equal(SecretChannelState.Degraded, deviceB.Sync.Status.SecretChannel);

        // ── O sadio chegou e ABRE; o veneno não virou envelope nenhum ────────────────────
        SecretEnvelope received = Assert.Single(await deviceB.ListEnvelopesAsync());
        Assert.Equal(good.EnvelopeId, received.EnvelopeId);
        using VaultSecret opened = await deviceB.OpenAsync(good.EnvelopeId);
        Assert.Equal(KeychainPassword, opened.RevealString());

        // E o host segue utilizável: o vínculo credencial→envelope está inteiro.
        CredentialRef? credOnB = await deviceB.Store.GetCredentialRefAsync(credId);
        Assert.Equal(good.EnvelopeId, credOnB?.SecretEnvelopeId);
    }

    private static string Base64(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
