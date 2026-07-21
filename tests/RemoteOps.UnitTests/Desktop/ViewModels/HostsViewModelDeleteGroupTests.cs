using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// Exclusão de grupo — a trava de "só grupo VAZIO" é o coração desta feature, não um detalhe de UX.
///
/// <para><b>Por que a trava é obrigatória:</b> <c>ILocalStore.DeleteGroupAsync</c> apaga SÓ a linha do
/// grupo (<c>DELETE FROM asset_groups</c>) e não toca nos ativos. Sem a trava, os equipamentos do grupo
/// ficariam com <c>group_id</c> apontando para um grupo inexistente — órfãos, invisíveis na tela e
/// propagados assim para os outros dispositivos pelo sync. Ou seja: perda de dado SILENCIOSA, o defeito
/// recorrente deste app.</para>
///
/// <para>Por isso os testes daqui fixam o comportamento observável pelo operador: o que NÃO é excluído,
/// o que a mensagem diz (inclusive a contagem, para ele saber o tamanho do trabalho) e que a falha do
/// store vira AVISO — nunca silêncio (lição da v1.2.23, quando excluir host falhava calado).</para>
/// </summary>
public sealed class HostsViewModelDeleteGroupTests
{
    private static SessionLauncher Launcher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static HostsViewModel NewVm(ILocalStore store) => new(store, Launcher(), "ws-local");

    private static Task<Asset> AddHostAsync(ILocalStore store, string groupId, string name)
        => store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = "ws-local",
            GroupId = groupId,
            Name = name,
        });

    /// <summary>Decorador que faz SÓ o <c>DeleteGroupAsync</c> falhar — simula cofre/DB indisponível.</summary>
    private sealed class FailingDeleteGroupStore : ILocalStore
    {
        private readonly ILocalStore _inner;

        public FailingDeleteGroupStore(ILocalStore inner) => _inner = inner;

        public Task DeleteGroupAsync(string id, CancellationToken ct = default)
            => throw new InvalidOperationException("banco indisponível (simulado)");

        public Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(string workspaceId, CancellationToken ct = default)
            => _inner.GetGroupsAsync(workspaceId, ct);

        public Task<AssetGroup> AddGroupAsync(string workspaceId, string name, string? parentId = null, CancellationToken ct = default)
            => _inner.AddGroupAsync(workspaceId, name, parentId, ct);

        public Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
            => _inner.RenameGroupAsync(id, newName, ct);

        public Task<AssetGroup> UpdateGroupAsync(AssetGroup group, CancellationToken ct = default)
            => _inner.UpdateGroupAsync(group, ct);

        public Task<IReadOnlyList<Asset>> GetAssetsAsync(string workspaceId, string? groupId = null, CancellationToken ct = default)
            => _inner.GetAssetsAsync(workspaceId, groupId, ct);

        public Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
            => _inner.GetAssetAsync(id, ct);

        public Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default)
            => _inner.AddAssetAsync(request, ct);

        public Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
            => _inner.UpdateAssetAsync(asset, ct);

        public Task DeleteAssetAsync(string id, CancellationToken ct = default)
            => _inner.DeleteAssetAsync(id, ct);

        public Task<Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
            => _inner.GetEndpointAsync(endpointId, ct);

        public Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => _inner.AddEndpointAsync(endpoint, ct);

        public Task<Endpoint> UpdateEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => _inner.UpdateEndpointAsync(endpoint, ct);

        public Task DeleteEndpointAsync(string id, CancellationToken ct = default)
            => _inner.DeleteEndpointAsync(id, ct);

        public Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(string workspaceId, CancellationToken ct = default)
            => _inner.GetCredentialRefsAsync(workspaceId, ct);

        public Task<CredentialRef?> GetCredentialRefAsync(string credentialRefId, CancellationToken ct = default)
            => _inner.GetCredentialRefAsync(credentialRefId, ct);

        public Task<CredentialRef> AddCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
            => _inner.AddCredentialRefAsync(credentialRef, ct);

        public Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
            => _inner.UpdateCredentialRefAsync(credentialRef, ct);

        public Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
            => _inner.DeleteCredentialRefAsync(id, ct);
    }

    // ── A trava: grupo COM equipamentos nunca é excluído ──────────────────────────────────────

    [Fact]
    public async Task ExcluirGrupo_ComEquipamentos_NaoExclui_E_Avisa_Com_A_Contagem()
    {
        var store = new InMemoryLocalStore();
        AssetGroup g = await store.AddGroupAsync("ws-local", "Innet");
        await AddHostAsync(store, g.Id, "r1");
        await AddHostAsync(store, g.Id, "r2");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();

        bool perguntou = false;
        vm.DeleteGroupConfirmationRequested += (_, req) => { perguntou = true; req.Confirmed = true; };
        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;

        await vm.DeleteGroupAsync();

        // Nem chega a perguntar: bloqueia antes, senão o operador confirmaria uma perda de dado.
        Assert.False(perguntou);
        Assert.NotNull(aviso);
        Assert.Contains("Innet", aviso, StringComparison.Ordinal);
        Assert.Contains("2 equipamento(s)", aviso, StringComparison.Ordinal);
        Assert.Contains("Mova ou exclua os equipamentos", aviso, StringComparison.Ordinal);

        // E o dado continua lá — grupo E equipamentos.
        Assert.Single(await store.GetGroupsAsync("ws-local"));
        Assert.Equal(2, (await store.GetAssetsAsync("ws-local", g.Id)).Count);
        Assert.Single(vm.Groups);
    }

    /// <summary>
    /// A contagem do card pode estar VELHA (o sync adiciona hosts entre o <c>LoadAsync</c> e o clique).
    /// A trava tem que consultar o store no momento da exclusão — senão um card marcado "0 hosts"
    /// autoriza apagar um grupo que já tem equipamentos, exatamente o cenário que a trava existe para
    /// impedir.
    /// </summary>
    [Fact]
    public async Task ExcluirGrupo_ComCardDesatualizado_ReconsultaOStore_E_Bloqueia()
    {
        var store = new InMemoryLocalStore();
        AssetGroup g = await store.AddGroupAsync("ws-local", "Innet");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();
        Assert.Equal(0, vm.SelectedGroup.HostCount); // card diz vazio…

        // …mas o sync trouxe um equipamento depois do card ser montado.
        await AddHostAsync(store, g.Id, "r1");

        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;
        vm.DeleteGroupConfirmationRequested += (_, req) => req.Confirmed = true;

        await vm.DeleteGroupAsync();

        Assert.NotNull(aviso);
        Assert.Contains("1 equipamento(s)", aviso, StringComparison.Ordinal);
        Assert.Single(await store.GetGroupsAsync("ws-local"));
    }

    /// <summary>
    /// CORRIDA: o diálogo de confirmação fica aberto por tempo humano e o sync grava no store em
    /// background. Se um host chega no grupo ENQUANTO o operador lê a pergunta, o "sim" não pode
    /// apagar — a checagem de vazio tem de ser refeita DEPOIS da confirmação, senão o equipamento
    /// vira órfão e o delete ainda propaga pros outros devices.
    /// </summary>
    [Fact]
    public async Task ExcluirGrupo_HostChegaDuranteAConfirmacao_NaoExclui_E_Avisa()
    {
        var store = new InMemoryLocalStore();
        AssetGroup g = await store.AddGroupAsync("ws-local", "Innet");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();

        // O grupo está vazio quando a pergunta abre — e o sync entrega um host antes da resposta.
        vm.DeleteGroupConfirmationRequested += (_, req) =>
        {
            AddHostAsync(store, g.Id, "r1").GetAwaiter().GetResult();
            req.Confirmed = true;
        };
        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;

        await vm.DeleteGroupAsync();

        Assert.NotNull(aviso);
        Assert.Contains("1 equipamento(s)", aviso, StringComparison.Ordinal);
        Assert.Single(await store.GetGroupsAsync("ws-local"));                  // grupo continua lá
        Assert.Single(await store.GetAssetsAsync("ws-local", g.Id));           // host intacto, sem órfão
    }

    // ── Grupo vazio: confirma e exclui ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExcluirGrupo_Vazio_Confirmado_Exclui_E_Recarrega_A_Lista()
    {
        var store = new InMemoryLocalStore();
        AssetGroup vazio = await store.AddGroupAsync("ws-local", "Temporário");
        await store.AddGroupAsync("ws-local", "Innet");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single(c => c.Id == vazio.Id);

        string? nomeConfirmado = null;
        vm.DeleteGroupConfirmationRequested += (_, req) => { nomeConfirmado = req.GroupName; req.Confirmed = true; };
        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;

        await vm.DeleteGroupAsync();

        Assert.Equal("Temporário", nomeConfirmado); // a confirmação NOMEIA o grupo
        Assert.Null(aviso);
        Assert.DoesNotContain(await store.GetGroupsAsync("ws-local"), x => x.Id == vazio.Id);
        Assert.Single(vm.Groups);                    // lista recarregada, só "Innet" restou
        Assert.Equal("Innet", vm.Groups.Single().Name);
        Assert.Null(vm.SelectedGroup);               // alvo some junto com o grupo
    }

    [Fact]
    public async Task ExcluirGrupo_Vazio_SemConfirmacao_NaoExclui()
    {
        var store = new InMemoryLocalStore();
        AssetGroup vazio = await store.AddGroupAsync("ws-local", "Temporário");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();

        vm.DeleteGroupConfirmationRequested += (_, req) => req.Confirmed = false;
        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;

        await vm.DeleteGroupAsync();

        Assert.Contains(await store.GetGroupsAsync("ws-local"), x => x.Id == vazio.Id);
        Assert.Single(vm.Groups);
        Assert.Null(aviso); // desistir não é falha: nada de caixa de aviso
    }

    /// <summary>
    /// Sem NENHUM assinante de confirmação a exclusão não pode acontecer "por omissão" — um shell que
    /// esqueceu de ligar o diálogo apagaria grupos sem o operador ver nada.
    /// </summary>
    [Fact]
    public async Task ExcluirGrupo_SemAssinanteDeConfirmacao_NaoExclui()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Temporário");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();

        await vm.DeleteGroupAsync();

        Assert.Single(await store.GetGroupsAsync("ws-local"));
    }

    // ── Falha do store vira aviso, nunca silêncio ─────────────────────────────────────────────

    [Fact]
    public async Task ExcluirGrupo_QuandoOStoreFalha_Avisa_E_Nao_Fica_Em_Silencio()
    {
        var inner = new InMemoryLocalStore();
        await inner.AddGroupAsync("ws-local", "Temporário");
        var store = new FailingDeleteGroupStore(inner);
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();

        vm.DeleteGroupConfirmationRequested += (_, req) => req.Confirmed = true;
        string? aviso = null;
        vm.DeleteGroupFailed += (_, m) => aviso = m;

        await vm.DeleteGroupAsync(); // não pode propagar exceção (o command é fire-and-forget)

        Assert.NotNull(aviso);
        Assert.Contains("Falha ao excluir o grupo", aviso, StringComparison.Ordinal);
        Assert.Contains("banco indisponível (simulado)", aviso, StringComparison.Ordinal);
        Assert.Single(await inner.GetGroupsAsync("ws-local")); // grupo continua lá
    }

    // ── Comando: habilitado só com alvo ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteGroupCommand_Desabilitado_Sem_Alvo_E_Habilitado_Com_Alvo()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Innet");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();

        Assert.False(vm.DeleteGroupCommand.CanExecute(null));

        vm.SelectedGroup = vm.Groups.Single();
        Assert.True(vm.DeleteGroupCommand.CanExecute(null));

        vm.SelectedGroup = null;
        Assert.False(vm.DeleteGroupCommand.CanExecute(null));
    }

    /// <summary>O command precisa mesmo CHAMAR a exclusão — binding certo com ação vazia é bug mudo.</summary>
    [Fact]
    public async Task DeleteGroupCommand_Execute_Exclui_O_Grupo_Vazio()
    {
        var store = new InMemoryLocalStore();
        AssetGroup vazio = await store.AddGroupAsync("ws-local", "Temporário");
        HostsViewModel vm = NewVm(store);
        await vm.LoadAsync();
        vm.SelectedGroup = vm.Groups.Single();
        vm.DeleteGroupConfirmationRequested += (_, req) => req.Confirmed = true;

        vm.DeleteGroupCommand.Execute(null);
        await Task.Delay(50);

        Assert.DoesNotContain(await store.GetGroupsAsync("ws-local"), x => x.Id == vazio.Id);
        Assert.Empty(vm.Groups);
    }
}
