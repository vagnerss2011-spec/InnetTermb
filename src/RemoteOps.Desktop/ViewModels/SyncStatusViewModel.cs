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

    public SyncStatusViewModel(ISyncController? controller = null)
    {
        _controller = controller;
        SyncNowCommand = new RelayCommand(_ => _ = RunSyncNowAsync(), _ => CanSyncNow);
    }

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
                SyncState.Synced => _conflictCount > 0
                    ? $"Sincronizado ({_conflictCount} conflito(s))"
                    : "Sincronizado",
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
        SyncState.Synced => _conflictCount > 0
            ? $"Sincronizado, mas há {_conflictCount} conflito(s) a resolver."
            : "Tudo sincronizado com a nuvem.",
        SyncState.Error => "Não foi possível sincronizar. Verifique a conexão e clique para tentar de novo.",
        _ => "Offline",
    };

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
        SyncNowCommand.RaiseCanExecuteChanged();
    }
}
