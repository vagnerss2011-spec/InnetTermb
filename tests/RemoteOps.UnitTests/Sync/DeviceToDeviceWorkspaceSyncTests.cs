using System.Linq;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Security.Account;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// <b>A razão de existir da Fase 1, na forma que o operador enxerga</b> (spec §1/§6/§12): "logo numa
/// conta em qualquer PC e TENHO TODOS OS MEUS DADOS — inclusive as senhas, decifráveis".
///
/// <para>O <see cref="DeviceToDeviceSecretSyncTests"/> prova a metade criptográfica (a senha viaja e
/// abre). Este prova a outra metade, sem a qual a primeira não serve pra nada: o HOST aparece na
/// lista, com o endereço certo, no grupo certo, ligado à credencial certa. Um cofre cheio de senhas
/// que não estão amarradas a nenhum host visível é, pro operador, um app vazio.</para>
///
/// <para>Nada de fake no caminho de dados: o store é o <c>SqlCipherLocalStore</c> de produção (o
/// mesmo que a UI lê), o applier é o de produção, a cripto é a real. O único fake é a REDE — e os
/// dois servidores fake recusam o que o backend real recusa.</para>
/// </summary>
public sealed class DeviceToDeviceWorkspaceSyncTests
{
    private const string AccountPassword = "senha-da-conta-do-operador-2026";
    private const string HostPassword = "s3nh@-do-NE8000-çÃo-#!$%";
    private const string Username = "operador-noc";

    private static string NewServerWorkspace() => Guid.NewGuid().ToString();

    /// <summary>
    /// O fluxo inteiro do §6. Device A monta o workspace de verdade (grupo + host classificado +
    /// endpoint endereçado + credencial com username + senha). Device B nasce com o BANCO e o COFRE
    /// vazios, tem só a senha da conta, e tem que terminar com tudo — incluindo a senha byte-idêntica.
    ///
    /// <para>Se este teste não passar, a fase não fechou. Não existe asserção mais importante aqui.</para>
    /// </summary>
    [Fact]
    public async Task DeviceB_ComBancoECofreVazios_E_SoASenhaDaConta_VeOHostCompletoEAbreASenha()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();

        // Se ISSO aparecer no changelog ou no canal de segredos, o E2EE está quebrado.
        changelog.Forbid(HostPassword);
        changelog.Forbid(AccountPassword);
        secrets.Forbid(HostPassword);
        secrets.Forbid(AccountPassword);

        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        string groupId;
        string assetId;
        string endpointId;
        string credentialId = Guid.NewGuid().ToString("n");
        string envelopeId;

        // ── Device A: o PC onde o operador cadastrou tudo ──────────────────────────────────
        await using (SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace))
        {
            AssetGroup group = await deviceA.Store.AddGroupAsync(SyncedDevice.VaultWorkspaceId, "Backbone");
            groupId = group.Id;

            Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = SyncedDevice.VaultWorkspaceId,
                GroupId = groupId,
                Name = "NE8000-CORE-01",
                Vendor = "Huawei",
                Model = "NE8000 M8",
                DeviceRole = DeviceRoles.Router,
                Site = "POP Centro",
                Tags = ["backbone", "critico"],
            });
            assetId = asset.Id;

            // A senha do equipamento: selada no cofre, e o envelope é referenciado pela credencial.
            SecretEnvelope sealedEnvelope = await deviceA.SealAsync(credentialId, HostPassword);
            envelopeId = sealedEnvelope.EnvelopeId;

            await deviceA.Store.AddCredentialRefAsync(new CredentialRef
            {
                Id = credentialId,
                Name = "(senha do dispositivo)",
                Type = CredentialTypes.Password,
                Scope = $"endpoint:{assetId}",
                Metadata = new CredentialMetadata { Username = Username },
                SecretEnvelopeId = envelopeId,
            });

            endpointId = Guid.NewGuid().ToString("n");
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = endpointId,
                AssetId = assetId,
                Protocol = "ssh",
                Fqdn = "ne8000-core-01.rede.local",
                Ipv4 = "10.20.30.40",
                Ipv6 = "2001:db8::40",
                Port = 2222,
                PreferIpv6 = false,
                CredentialRefId = credentialId,
                Profile = new EndpointProfile
                {
                    SshAlgorithmProfile = "strict",
                    BackspaceMode = TerminalBackspaceModes.ControlH,
                },
            });

            await deviceA.SyncOnceAsync();
        }

        // ── Device B: outro PC. Só a senha da conta + o escrow que o servidor guarda. ──────
        byte[] amkOnDeviceB = keys.UnwrapAmkWithPassword(
            AccountPassword, enrollment.Argon2Salt, enrollment.Params, enrollment.WrappedAmkPwd);

        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", amkOnDeviceB, changelog, secrets, serverWorkspace);

        // O device B começa vazio DE VERDADE — senão o teste provaria nada.
        Assert.Empty(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Empty(await deviceB.Store.GetGroupsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Empty(await deviceB.EnvelopeStore.ListEnvelopesAsync(SyncedDevice.VaultWorkspaceId));

        await deviceB.SyncOnceAsync();

        // ── 1. O GRUPO aparece ────────────────────────────────────────────────────────────
        AssetGroup receivedGroup = Assert.Single(
            await deviceB.Store.GetGroupsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Equal(groupId, receivedGroup.Id);
        Assert.Equal("Backbone", receivedGroup.Name);

        // ── 2. O HOST aparece na lista, classificado ──────────────────────────────────────
        Asset receivedAsset = Assert.Single(
            await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Equal(assetId, receivedAsset.Id);
        Assert.Equal("NE8000-CORE-01", receivedAsset.Name);
        Assert.Equal(groupId, receivedAsset.GroupId);        // dentro do grupo certo
        Assert.Equal("Huawei", receivedAsset.Vendor);
        Assert.Equal("NE8000 M8", receivedAsset.Model);
        Assert.Equal(DeviceRoles.Router, receivedAsset.DeviceRole); // o classificador é novo
        Assert.Equal("POP Centro", receivedAsset.Site);
        Assert.Equal(["backbone", "critico"], receivedAsset.Tags);

        // ── 3. O ENDPOINT com o ENDEREÇO certo (sem isso não dá pra conectar) ─────────────
        Endpoint receivedEndpoint = Assert.Single(receivedAsset.Endpoints);
        Assert.Equal(endpointId, receivedEndpoint.Id);
        Assert.Equal(assetId, receivedEndpoint.AssetId);
        Assert.Equal("ssh", receivedEndpoint.Protocol);
        Assert.Equal("ne8000-core-01.rede.local", receivedEndpoint.Fqdn);
        Assert.Equal("10.20.30.40", receivedEndpoint.Ipv4);
        Assert.Equal("2001:db8::40", receivedEndpoint.Ipv6);
        Assert.Equal(2222, receivedEndpoint.Port);
        Assert.False(receivedEndpoint.PreferIpv6);
        Assert.Equal("strict", receivedEndpoint.Profile?.SshAlgorithmProfile);
        Assert.Equal(TerminalBackspaceModes.ControlH, receivedEndpoint.Profile?.BackspaceMode);

        // ── 4. O LINK host → credencial ───────────────────────────────────────────────────
        Assert.Equal(credentialId, receivedEndpoint.CredentialRefId);

        CredentialRef? receivedCred = await deviceB.Store.GetCredentialRefAsync(credentialId);
        Assert.NotNull(receivedCred);
        Assert.Equal(CredentialTypes.Password, receivedCred.Type);
        Assert.Equal($"endpoint:{assetId}", receivedCred.Scope);
        Assert.Equal(Username, receivedCred.Metadata?.Username); // o username mora no metadata_json
        Assert.Equal(envelopeId, receivedCred.SecretEnvelopeId);

        // ── 5. E a SENHA abre, byte-idêntica. O objetivo da fase. ─────────────────────────
        using VaultSecret opened = await deviceB.OpenAsync(receivedCred.SecretEnvelopeId!);
        Assert.Equal(HostPassword, opened.RevealString());
    }

    /// <summary>
    /// Re-sync é no-op: rodar o ciclo de novo não duplica host, não zera campo e não reverte versão.
    /// O device B fica ligado o dia inteiro — se cada ciclo mexesse no estado, o app "piscaria".
    /// </summary>
    [Fact]
    public async Task ResyncEIdempotente_NaoDuplicaNemZeraCampos()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        await using (SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace))
        {
            Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = SyncedDevice.VaultWorkspaceId,
                Name = "SW-ACESSO-07",
                Vendor = "MikroTik",
                DeviceRole = DeviceRoles.Switch,
                Tags = ["acesso"],
            });
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = Guid.NewGuid().ToString("n"),
                AssetId = asset.Id,
                Protocol = "ssh",
                Ipv4 = "192.168.1.7",
                Port = 22,
            });
            await deviceA.SyncOnceAsync();
        }

        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", enrollment.Amk, changelog, secrets, serverWorkspace);

        await deviceB.SyncOnceAsync();
        await deviceB.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        Asset asset2 = Assert.Single(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Equal("SW-ACESSO-07", asset2.Name);
        Assert.Equal("MikroTik", asset2.Vendor);
        Assert.Equal(DeviceRoles.Switch, asset2.DeviceRole);
        Assert.Equal(["acesso"], asset2.Tags);
        Assert.Single(asset2.Endpoints); // um host, um endpoint — não três.
        Assert.Equal("192.168.1.7", asset2.Endpoints[0].Ipv4);
    }

    /// <summary>
    /// O delete propaga: o host apagado no A some no B. Um host fantasma que não existe mais é pior
    /// que um host ausente — o operador tentaria conectar nele.
    /// </summary>
    [Fact]
    public async Task DeleteNoDeviceA_ApagaOHostNoDeviceB()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        string assetId;
        string groupId;
        await using SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace);
        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", enrollment.Amk, changelog, secrets, serverWorkspace);

        AssetGroup group = await deviceA.Store.AddGroupAsync(SyncedDevice.VaultWorkspaceId, "Temporário");
        groupId = group.Id;
        Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = SyncedDevice.VaultWorkspaceId,
            GroupId = groupId,
            Name = "host-que-vai-sumir",
        });
        assetId = asset.Id;
        await deviceA.Store.AddEndpointAsync(new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = assetId,
            Protocol = "ssh",
            Ipv4 = "10.0.0.9",
            Port = 22,
        });
        await deviceA.SyncOnceAsync();

        await deviceB.SyncOnceAsync();
        Assert.Single(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));

        // Apaga no A e propaga.
        await deviceA.Store.DeleteAssetAsync(assetId);
        await deviceA.Store.DeleteGroupAsync(groupId);
        await deviceA.SyncOnceAsync();

        await deviceB.SyncOnceAsync();
        Assert.Empty(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Empty(await deviceB.Store.GetGroupsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Null(await deviceB.Store.GetAssetAsync(assetId));
    }

    /// <summary>
    /// Update propaga: renomear/reendereçar no A chega no B. É o caso do dia a dia — o host já existe
    /// nos dois lados e MUDA.
    /// </summary>
    [Fact]
    public async Task UpdateNoDeviceA_ChegaNoDeviceB()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        await using SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace);
        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", enrollment.Amk, changelog, secrets, serverWorkspace);

        AssetGroup group = await deviceA.Store.AddGroupAsync(SyncedDevice.VaultWorkspaceId, "Nome Velho");
        Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = SyncedDevice.VaultWorkspaceId,
            GroupId = group.Id,
            Name = "host-nome-velho",
            Vendor = "Huawei",
        });
        string endpointId = Guid.NewGuid().ToString("n");
        await deviceA.Store.AddEndpointAsync(new Endpoint
        {
            Id = endpointId,
            AssetId = asset.Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.1",
            Port = 22,
        });
        await deviceA.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        // Renomeia o grupo (patch PARCIAL: só {name}), muda o host e reendereça o endpoint.
        await deviceA.Store.RenameGroupAsync(group.Id, "Nome Novo");
        await deviceA.Store.UpdateAssetAsync(new Asset
        {
            Id = asset.Id,
            WorkspaceId = SyncedDevice.VaultWorkspaceId,
            GroupId = group.Id,
            Name = "host-nome-novo",
            Vendor = "Cisco",
            Model = "ASR 920",
            DeviceRole = DeviceRoles.Router,
            Site = "POP Sul",
            Tags = ["novo"],
            Version = 1,
        });
        await deviceA.Store.UpdateEndpointAsync(new Endpoint
        {
            Id = endpointId,
            AssetId = asset.Id,
            Protocol = "telnet",
            Ipv4 = "10.0.0.2",
            Port = 23,
        });
        await deviceA.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        AssetGroup g = Assert.Single(await deviceB.Store.GetGroupsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Equal("Nome Novo", g.Name);

        Asset a = Assert.Single(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.Equal("host-nome-novo", a.Name);
        Assert.Equal("Cisco", a.Vendor);
        Assert.Equal("ASR 920", a.Model);
        Assert.Equal(DeviceRoles.Router, a.DeviceRole);
        Assert.Equal("POP Sul", a.Site);
        Assert.Equal(["novo"], a.Tags);

        Endpoint ep = Assert.Single(a.Endpoints);
        Assert.Equal("telnet", ep.Protocol);
        Assert.Equal("10.0.0.2", ep.Ipv4);
        Assert.Equal(23, ep.Port);
    }

    /// <summary>
    /// O changelog NUNCA carrega segredo (ADR-003). O <see cref="FakeChangelogApi.Forbid"/> já varre
    /// cada patch, mas aqui a asserção é explícita e no negativo: nenhum <c>SecretEnvelope</c> entrou
    /// no changelog — o segredo foi pelo canal <c>/secrets</c>, e só por ele.
    /// </summary>
    [Fact]
    public async Task NenhumSegredoNoChangelog_OEnvelopeVaiSoPeloCanalDeSecrets()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        changelog.Forbid(HostPassword);
        secrets.Forbid(HostPassword);

        await using SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace);

        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope env = await deviceA.SealAsync(credId, HostPassword);
        await deviceA.Store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = "cred",
            Type = CredentialTypes.Password,
            Metadata = new CredentialMetadata { Username = Username },
            SecretEnvelopeId = env.EnvelopeId,
        });
        await deviceA.SyncOnceAsync();

        // O changelog só viu metadados — nunca o tipo SecretEnvelope.
        Assert.DoesNotContain("SecretEnvelope", changelog.StoredEntityTypes);
        Assert.Contains("credential_ref", changelog.StoredEntityTypes);

        // E o envelope subiu pelo canal certo.
        Assert.Single(secrets.Accepted);
    }
}
