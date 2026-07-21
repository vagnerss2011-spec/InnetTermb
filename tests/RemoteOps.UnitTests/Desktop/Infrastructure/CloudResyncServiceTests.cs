using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

/// <summary>
/// "Reenviar tudo para a nuvem" — o reparo do acervo que subiu incompleto.
///
/// O que estes testes protegem, em ordem de importância:
/// <list type="number">
///   <item><b>ORDEM</b>: o pull acontece ANTES do primeiro update. Sem isso, o re-emit sobe com
///   <c>base_version</c> atrasada e o servidor rejeita cada item como conflito — 700 devices viram
///   700 conflitos em vez de reparo. É o teste que mais importa.</item>
///   <item><b>IDEMPOTÊNCIA</b>: clicar duas vezes não pode duplicar nada. O reparo é um botão que o
///   operador aflito vai clicar mais de uma vez.</item>
///   <item><b>COBERTURA</b>: cada grupo, ativo, endpoint e credencial passa pelo caminho de update.</item>
/// </list>
/// </summary>
public sealed class CloudResyncServiceTests
{
    private const string Ws = "ws-local";

    // Um diário ÚNICO, compartilhado pelo store e pelo controlador de sync: é o que permite afirmar
    // ORDEM entre coisas de camadas diferentes ("o sync veio antes do primeiro update?"), coisa que
    // dois contadores separados não conseguem dizer.
    private sealed class Journal
    {
        public List<string> Entries { get; } = [];

        public void Add(string entry) => Entries.Add(entry);

        public int Count(string prefix) =>
            Entries.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));

        public int FirstIndex(string prefix) =>
            Entries.FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Decorador de <see cref="ILocalStore"/> que anota no diário por onde o reenvio passou. Decorar
    /// o <see cref="InMemoryLocalStore"/> (em vez de um fake do zero) mantém o comportamento REAL do
    /// store — o teste de idempotência precisa que os dados continuem lá depois do reenvio.
    /// </summary>
    private sealed class RecordingLocalStore : ILocalStore
    {
        private readonly ILocalStore _inner;
        private readonly Journal _journal;
        private readonly string? _poisonedEndpointId;

        public RecordingLocalStore(ILocalStore inner, Journal journal, string? poisonedEndpointId = null)
        {
            _inner = inner;
            _journal = journal;
            _poisonedEndpointId = poisonedEndpointId;
        }

        public Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(string workspaceId, CancellationToken ct = default)
            => _inner.GetGroupsAsync(workspaceId, ct);

        public Task<AssetGroup> AddGroupAsync(string workspaceId, string name, string? parentId = null, CancellationToken ct = default)
        {
            _journal.Add("add-group");
            return _inner.AddGroupAsync(workspaceId, name, parentId, ct);
        }

        public Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
        {
            _journal.Add("rename-group:" + id);
            return _inner.RenameGroupAsync(id, newName, ct);
        }

        public Task<AssetGroup> UpdateGroupAsync(AssetGroup group, CancellationToken ct = default)
        {
            _journal.Add("group:" + group.Id);
            return _inner.UpdateGroupAsync(group, ct);
        }

        public Task DeleteGroupAsync(string id, CancellationToken ct = default)
        {
            _journal.Add("delete-group:" + id);
            return _inner.DeleteGroupAsync(id, ct);
        }

        public Task<IReadOnlyList<Asset>> GetAssetsAsync(string workspaceId, string? groupId = null, CancellationToken ct = default)
            => _inner.GetAssetsAsync(workspaceId, groupId, ct);

        public Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
            => _inner.GetAssetAsync(id, ct);

        public Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default)
        {
            _journal.Add("add-asset");
            return _inner.AddAssetAsync(request, ct);
        }

        public Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
        {
            _journal.Add("asset:" + asset.Id);
            return _inner.UpdateAssetAsync(asset, ct);
        }

        public Task DeleteAssetAsync(string id, CancellationToken ct = default)
        {
            _journal.Add("delete-asset:" + id);
            return _inner.DeleteAssetAsync(id, ct);
        }

        public Task<Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
            => _inner.GetEndpointAsync(endpointId, ct);

        public Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
        {
            _journal.Add("add-endpoint");
            return _inner.AddEndpointAsync(endpoint, ct);
        }

        public Task<Endpoint> UpdateEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
        {
            _journal.Add("endpoint:" + endpoint.Id);
            if (endpoint.Id == _poisonedEndpointId)
            {
                throw new InvalidOperationException("linha venenosa simulada");
            }

            return _inner.UpdateEndpointAsync(endpoint, ct);
        }

        public Task DeleteEndpointAsync(string id, CancellationToken ct = default)
        {
            _journal.Add("delete-endpoint:" + id);
            return _inner.DeleteEndpointAsync(id, ct);
        }

        public Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(string workspaceId, CancellationToken ct = default)
            => _inner.GetCredentialRefsAsync(workspaceId, ct);

        public Task<CredentialRef?> GetCredentialRefAsync(string credentialRefId, CancellationToken ct = default)
            => _inner.GetCredentialRefAsync(credentialRefId, ct);

        public Task<CredentialRef> AddCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
        {
            _journal.Add("add-cred");
            return _inner.AddCredentialRefAsync(credentialRef, ct);
        }

        public Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
        {
            _journal.Add("cred:" + credentialRef.Id);
            return _inner.UpdateCredentialRefAsync(credentialRef, ct);
        }

        public Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
        {
            _journal.Add("delete-cred:" + id);
            return _inner.DeleteCredentialRefAsync(id, ct);
        }
    }

    private sealed class JournalingSyncController : ISyncController
    {
        private readonly Journal _journal;

        public JournalingSyncController(Journal journal) => _journal = journal;

        /// <summary>Em qual chamada devolver <c>false</c> (1 = pull inicial, 2 = drenagem final).</summary>
        public int? FailOnCall { get; init; }

        private int _calls;

        // false, não exceção: é assim que o controlador real reporta ciclo falhado (o orquestrador
        // engole a falha — offline-first) e é esse sinal que o serviço tem que respeitar.
        public Task<bool> SyncNowAsync(CancellationToken ct = default)
        {
            _journal.Add("sync");
            return Task.FromResult(++_calls != FailOnCall);
        }

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Acervo do enunciado: 2 grupos, 3 ativos, 4 endpoints, 2 credenciais.</summary>
    private static async Task<(InMemoryLocalStore Inner, string PoisonEndpointId)> SeedAsync()
    {
        var inner = new InMemoryLocalStore();

        AssetGroup g1 = await inner.AddGroupAsync(Ws, "POPs");
        await inner.AddGroupAsync(Ws, "Clientes", g1.Id);

        var assets = new List<Asset>();
        foreach (string name in new[] { "olt-01", "bras-01", "sw-01" })
        {
            assets.Add(await inner.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = Ws,
                Name = name,
                GroupId = g1.Id,
            }));
        }

        await inner.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cred-1",
            Name = "admin NOC",
            Type = "password",
            Scope = Ws,
            SecretEnvelopeId = "env-1",
        });
        await inner.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cred-2",
            Name = "chave OLT",
            Type = "privateKey",
            Scope = Ws,
            SecretEnvelopeId = "env-2",
        });

        // 4 endpoints em 3 ativos (o primeiro tem dois: SSH e Telnet) — o vínculo com a credencial é
        // exatamente o campo que sumiu em produção.
        await inner.AddEndpointAsync(new Endpoint
        {
            Id = "ep-1",
            AssetId = assets[0].Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.1",
            Port = 22,
            CredentialRefId = "cred-1",
        });
        await inner.AddEndpointAsync(new Endpoint
        {
            Id = "ep-2",
            AssetId = assets[0].Id,
            Protocol = "telnet",
            Ipv4 = "10.0.0.1",
            Port = 23,
            CredentialRefId = "cred-1",
        });
        await inner.AddEndpointAsync(new Endpoint
        {
            Id = "ep-3",
            AssetId = assets[1].Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.2",
            Port = 22,
            CredentialRefId = "cred-2",
        });
        await inner.AddEndpointAsync(new Endpoint
        {
            Id = "ep-4",
            AssetId = assets[2].Id,
            Protocol = "ssh",
            Ipv4 = "10.0.0.3",
            Port = 22,
            CredentialRefId = "cred-1",
        });

        return (inner, "ep-3");
    }

    private static async Task<(CloudResyncService Service, Journal Journal, InMemoryLocalStore Inner)> BuildAsync(
        bool withCloud = true, bool poisoned = false, int? failSyncOnCall = null)
    {
        (InMemoryLocalStore inner, string poisonId) = await SeedAsync();
        var journal = new Journal();
        var store = new RecordingLocalStore(inner, journal, poisoned ? poisonId : null);
        ISyncController? sync = withCloud
            ? new JournalingSyncController(journal) { FailOnCall = failSyncOnCall }
            : null;
        return (new CloudResyncService(store, Ws, sync), journal, inner);
    }

    [Fact]
    public async Task Resync_ReEmits_Every_Entity_Through_The_Update_Path()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync();

        ResyncResult result = await service.ResyncAllAsync();

        Assert.Equal(2, journal.Count("group:"));
        Assert.Equal(3, journal.Count("asset:"));
        Assert.Equal(4, journal.Count("endpoint:"));
        Assert.Equal(2, journal.Count("cred:"));
        Assert.True(result.Ran);
        Assert.Equal(11, result.ReEmitted);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// O teste que vale por todos: o pull TEM que vir antes do primeiro update. O servidor recusa
    /// <c>base_version &lt; versão dele</c>; sem alinhar antes, o reparo vira uma enxurrada de
    /// conflitos. O sync final (drenagem do outbox) fecha o ciclo.
    /// </summary>
    [Fact]
    public async Task Resync_Pulls_Before_ReEmitting_And_Drains_After()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync();

        await service.ResyncAllAsync();

        Assert.Equal("sync", journal.Entries[0]);
        Assert.Equal("sync", journal.Entries[^1]);
        Assert.Equal(2, journal.Count("sync"));

        // Todo update caiu DEPOIS do pull inicial e ANTES da drenagem final.
        int firstUpdate = journal.FirstIndex("group:");
        Assert.True(firstUpdate > 0, "nenhum update, ou update antes do pull inicial");
        Assert.True(journal.Entries.FindLastIndex(e => e.StartsWith("cred:", StringComparison.Ordinal))
            < journal.Entries.Count - 1);
    }

    [Fact]
    public async Task Resync_Is_Idempotent()
    {
        (CloudResyncService service, Journal journal, InMemoryLocalStore inner) = await BuildAsync();

        await service.ResyncAllAsync();
        await service.ResyncAllAsync();

        // Nada foi CRIADO nem APAGADO: o reenvio só re-emite o que já existe.
        Assert.Equal(0, journal.Count("add-"));
        Assert.Equal(0, journal.Count("delete-"));

        Assert.Equal(2, (await inner.GetGroupsAsync(Ws)).Count);
        Assert.Equal(3, (await inner.GetAssetsAsync(Ws)).Count);
        Assert.Equal(2, (await inner.GetCredentialRefsAsync(Ws)).Count);
        Assert.Equal(4, (await inner.GetAssetsAsync(Ws)).Sum(a => a.Endpoints.Count));

        // A segunda passada re-emite exatamente o mesmo tanto (versão+1, sem efeito colateral).
        Assert.Equal(4, journal.Count("group:"));
        Assert.Equal(8, journal.Count("endpoint:"));
    }

    [Fact]
    public async Task Resync_Reports_Progress()
    {
        (CloudResyncService service, _, _) = await BuildAsync();
        var seen = new List<ResyncProgress>();

        // IProgress direto, e não Progress<T>: aquele posta no SynchronizationContext e, sem um
        // instalado (xUnit), as callbacks caem no pool e chegam fora de ordem — o teste de
        // monotonicidade ficaria intermitente por culpa do arnês, não do código.
        ResyncResult result = await service.ResyncAllAsync(new DirectProgress(seen));

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(11, p.Total));
        Assert.Equal(11, seen[^1].Completed);
        Assert.Equal(11, result.ReEmitted);

        // Monotônico e nunca ultrapassa o total.
        for (int i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].Completed >= seen[i - 1].Completed, "progresso regrediu");
            Assert.True(seen[i].Completed <= seen[i].Total, "progresso passou do total");
        }
    }

    private sealed class DirectProgress : IProgress<ResyncProgress>
    {
        private readonly List<ResyncProgress> _seen;

        public DirectProgress(List<ResyncProgress> seen) => _seen = seen;

        public void Report(ResyncProgress value) => _seen.Add(value);
    }

    [Fact]
    public async Task Resync_Without_Cloud_Does_Nothing()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync(withCloud: false);

        Assert.False(service.CanResync);

        ResyncResult result = await service.ResyncAllAsync();

        Assert.False(result.Ran);
        Assert.Empty(journal.Entries); // nem sync, nem update: não há para onde reenviar.
    }

    /// <summary>
    /// O pull inicial FALHOU (nuvem fora, token vencido): o reenvio aborta ANTES de re-emitir
    /// qualquer coisa. O orquestrador real engole a falha do ciclo (offline-first) e a devolve como
    /// <c>false</c> — se o serviço ignorar esse sinal, ele enche o outbox de patches com
    /// <c>base_version</c> atrasada (a enxurrada de conflitos que o pull existe pra impedir) e a
    /// tela ainda diz "concluído" com a nuvem fora do ar.
    /// </summary>
    [Fact]
    public async Task Resync_Aborts_Before_ReEmitting_When_The_Initial_Pull_Fails()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync(failSyncOnCall: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResyncAllAsync());

        // Só a tentativa de pull no diário: nenhum update saiu, nada entrou no outbox.
        string entry = Assert.Single(journal.Entries);
        Assert.Equal("sync", entry);
    }

    /// <summary>
    /// A drenagem final FALHOU: o resultado não pode ser "concluído" — o acervo re-emitido está no
    /// outbox, não na nuvem. A exceção vira "não foi possível" na tela; o laço de fundo (ou um novo
    /// clique, idempotente) entrega o que ficou.
    /// </summary>
    [Fact]
    public async Task Resync_Does_Not_Claim_Success_When_The_Final_Drain_Fails()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync(failSyncOnCall: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResyncAllAsync());

        // O re-emit aconteceu por inteiro (fica no outbox à espera do sync de fundo)...
        Assert.Equal(2, journal.Count("group:"));
        Assert.Equal(3, journal.Count("asset:"));
        Assert.Equal(4, journal.Count("endpoint:"));
        Assert.Equal(2, journal.Count("cred:"));
        // ...e a drenagem foi TENTADA — a falha veio dela, não de um atalho que a pulou.
        Assert.Equal(2, journal.Count("sync"));
    }

    /// <summary>
    /// Mesma lição do canal de segredos: uma linha problemática não pode levar o reparo dos outros
    /// 699 junto. Ela é pulada, contada, e o resto sobe.
    /// </summary>
    [Fact]
    public async Task Resync_Skips_The_Poisoned_Row_And_Finishes_The_Rest()
    {
        (CloudResyncService service, Journal journal, _) = await BuildAsync(poisoned: true);

        ResyncResult result = await service.ResyncAllAsync();

        Assert.True(result.Ran);
        Assert.Equal(1, result.Failed);
        Assert.Equal(10, result.ReEmitted);
        Assert.Equal(4, journal.Count("endpoint:")); // tentou os 4
        Assert.Equal(2, journal.Count("cred:"));     // e seguiu para as credenciais
        Assert.Equal(2, journal.Count("sync"));      // e drenou no fim
    }
}
