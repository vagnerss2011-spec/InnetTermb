using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Estado do cloud sync PRA UI (Fase 2, item B): traduz o <see cref="SyncStatus"/> do orquestrador em
/// texto pt-BR + indicador (Offline/Sincronizando/Sincronizado/Erro) e expõe o botão "Sincronizar
/// agora" (push+pull via <see cref="ISyncController"/>).
///
/// <para><b>Offline-first (ADR-002):</b> sem conta/nuvem não há controlador — <see cref="HasCloud"/> é
/// <c>false</c> e o comando fica DESABILITADO. O app segue idêntico ao de hoje; o indicador só mostra
/// "Offline".</para>
///
/// <para><b>Thread-affinity:</b> quem chama <see cref="Apply"/>/<see cref="AttachController"/> (o App,
/// a partir do StatusChanged que vem da thread de fundo do sync) DEVE marshalar pro Dispatcher antes —
/// mesma disciplina do item 1 da Fase 2. O comando roda o sync fora da UI thread (await).</para>
/// </summary>
public sealed class SyncStatusViewModel : BaseViewModel
{
    private ISyncController? _controller;
    private SyncState _state = SyncState.Offline;
    private int _conflictCount;
    private bool _isBusy;

    // Começa em false: enquanto o canal de hints não confirmou que subiu, o que vale é o laço por
    // intervalo. Prometer tempo real antes da confirmação mentiria justo no caso que o operador
    // precisa enxergar — a rede que bloqueia WebSocket.
    private bool _isRealTime;

    public SyncStatusViewModel(ISyncController? controller = null)
    {
        _controller = controller;
        SyncNowCommand = new RelayCommand(_ => _ = RunSyncNowAsync(), _ => CanSyncNow);
        ShowConflictsCommand = new RelayCommand(_ => ConflictsRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Pedido de abrir a lista do que não subiu. A VM não abre janela — quem faz isso é a janela
    /// principal (mesma divisão do aviso de atualização).
    /// </summary>
    public event EventHandler? ConflictsRequested;

    /// <summary>Comando do aviso clicável "N alterações não subiram".</summary>
    public RelayCommand ShowConflictsCommand { get; }

    /// <summary>Comando do botão/ícone "Sincronizar agora" (push+pull). Desabilitado sem nuvem ou ocupado.</summary>
    public RelayCommand SyncNowCommand { get; }

    /// <summary>Há nuvem configurada/ativa? (Sem ela os comandos ficam desabilitados — offline-first.)</summary>
    public bool HasCloud => _controller is not null;

    /// <summary>Um ciclo forçado está em andamento (evita cliques repetidos e mostra "Sincronizando…").</summary>
    public bool IsBusy => _isBusy;

    public bool IsOffline => _state == SyncState.Offline && !_isBusy;
    public bool IsSyncing => _state == SyncState.Syncing || _isBusy;
    public bool IsSynced => _state == SyncState.Synced && !_isBusy;
    public bool IsError => _state == SyncState.Error && !_isBusy;

    /// <summary>Texto curto do indicador (pt-BR).</summary>
    public string StatusText
    {
        get
        {
            if (_isBusy || _state == SyncState.Syncing)
            {
                return "Sincronizando…";
            }

            return _state switch
            {
                SyncState.Offline => "Offline",
                // A contagem saiu daqui: virou um aviso PRÓPRIO e clicável na barra (ConflictText).
                // Embutida no status, ela dizia "conflito(s)" — jargão que não explica nada — e ainda
                // dava a impressão de trabalho pendente sem oferecer nenhuma ação.
                SyncState.Synced => "Sincronizado",
                SyncState.Error => "Erro de sincronização",
                _ => "Offline",
            };
        }
    }

    /// <summary>Detalhe acionável (tooltip) — pt-BR, orienta o operador conforme o estado.</summary>
    public string StatusDetail => _state switch
    {
        _ when _isBusy || _state == SyncState.Syncing => "Enviando e recebendo alterações…",
        SyncState.Offline => HasCloud
            ? "Sem sincronização no momento. Clique para sincronizar agora."
            : "Nuvem não configurada — o RemoteOps está trabalhando só neste computador.",
        SyncState.Synced => "Tudo sincronizado com a nuvem.",
        SyncState.Error => "Não foi possível sincronizar. Verifique a conexão e clique para tentar de novo.",
        _ => "Offline",
    };

    /// <summary>
    /// Como as alterações da outra ponta chegam AGORA: pelo canal de hints ("Tempo real") ou só no
    /// próximo tick do laço ("Periódico"). Fica ao lado do status porque "Sincronizado" tem a mesma
    /// cara nos dois mundos, enquanto o atraso máximo pula de segundos para dezenas deles.
    ///
    /// <para>É a ÚNICA pista que o operador tem em campo: a URL do hub carrega o JWT e por isso não
    /// pode ir para o log (ADR-013) — um canal derrubado por firewall não deixaria outro rastro.</para>
    /// </summary>
    public string ChannelText => _isRealTime ? "Tempo real" : "Periódico";

    /// <summary>Detalhe do canal (tooltip) — explica ao operador o que muda na prática.</summary>
    public string ChannelDetail => _isRealTime
        ? "As alterações feitas nos outros computadores chegam em segundos."
        : "Sem canal em tempo real — as alterações chegam na próxima verificação periódica.";

    /// <summary>
    /// Reflete o estado do canal de hints. Quem chama (o App, a partir do <c>RealTimeChanged</c>, que
    /// vem da thread do SignalR) DEVE marshalar pro Dispatcher antes — mesma disciplina do
    /// <see cref="Apply"/>.
    /// </summary>
    public void SetRealTime(bool value)
    {
        if (_isRealTime == value)
        {
            return;
        }

        _isRealTime = value;

        // Os dois são DERIVADOS de _isRealTime: sem o raise explícito o texto congelaria na tela
        // (Set() avisaria sobre o campo, que não tem binding nenhum).
        RaisePropertyChanged(nameof(ChannelText));
        RaisePropertyChanged(nameof(ChannelDetail));
    }

    /// <summary>
    /// Liga o controlador real quando o sync sobe (o App chama depois de criar a sessão). Vira o
    /// <see cref="HasCloud"/> pra <c>true</c> e habilita "Sincronizar agora".
    /// </summary>
    public void AttachController(ISyncController controller)
    {
        _controller = controller;
        RaisePropertyChanged(nameof(HasCloud));
        SyncNowCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Há conflitos registrados? Controla a visibilidade do aviso clicável na barra.</summary>
    public bool HasConflicts => _conflictCount > 0;

    /// <summary>
    /// Texto do aviso na barra. Fala do EFEITO ("não subiu"), não do jargão ("conflito"): o operador
    /// precisa entender que perdeu uma edição, não que existe um estado interno chamado conflito.
    /// </summary>
    public string ConflictText => _conflictCount switch
    {
        <= 0 => string.Empty,
        1 => "1 alteração não subiu",
        _ => $"{_conflictCount} alterações não subiram",
    };

    /// <summary>Carrega os conflitos para exibição. NUNCA lança: falha vira lista vazia.</summary>
    public async Task<IReadOnlyList<SyncConflictItem>> LoadConflictsAsync(int limit = 200)
    {
        if (_controller is not { } controller)
        {
            return [];
        }

        try
        {
            return await controller.GetConflictsAsync(limit);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Dispensa os conflitos e zera o indicador. É isto que faz o contador significar PENDÊNCIA: antes
    /// era um total histórico que só crescia e nunca voltava a zero, exibido como trabalho a fazer.
    /// </summary>
    public async Task DismissConflictsAsync()
    {
        if (_controller is not { } controller)
        {
            return;
        }

        try
        {
            await controller.DismissConflictsAsync();
            _conflictCount = 0;
            RaiseAllStatusProps();
        }
        catch (Exception)
        {
            // Falhou ao limpar: o indicador segue mostrando o que existe — melhor do que mentir zero.
        }
    }

    /// <summary>Reflete o estado vindo do orquestrador (Offline/Syncing/Synced/Error + conflitos).</summary>
    public void Apply(SyncStatus status)
    {
        _state = status.State;
        _conflictCount = status.ConflictCount;
        RaiseAllStatusProps();
    }

    private bool CanSyncNow => HasCloud && !_isBusy;

    private async Task RunSyncNowAsync()
    {
        if (_controller is not { } controller || _isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await controller.SyncNowAsync();
        }
        catch
        {
            // O estado de erro chega pelo StatusChanged do orquestrador (→ Apply). Não estoura pra UI:
            // o botão volta a ficar clicável no finally pra o operador tentar de novo.
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        if (_isBusy == value)
        {
            return;
        }

        _isBusy = value;
        RaiseAllStatusProps();
    }

    private void RaiseAllStatusProps()
    {
        RaisePropertyChanged(nameof(IsBusy));
        RaisePropertyChanged(nameof(IsOffline));
        RaisePropertyChanged(nameof(IsSyncing));
        RaisePropertyChanged(nameof(IsSynced));
        RaisePropertyChanged(nameof(IsError));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(StatusDetail));
        RaisePropertyChanged(nameof(HasConflicts));
        RaisePropertyChanged(nameof(ConflictText));
        SyncNowCommand.RaiseCanExecuteChanged();
    }
}
