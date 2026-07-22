using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly TimeProvider _clock;

    private ISyncController? _controller;
    private SyncState _state = SyncState.Offline;
    private int _conflictCount;
    private SecretChannelState _secretChannel = SecretChannelState.Idle;
    private bool _isBusy;

    // Começa em false: enquanto o canal de hints não confirmou que subiu, o que vale é o laço por
    // intervalo. Prometer tempo real antes da confirmação mentiria justo no caso que o operador
    // precisa enxergar — a rede que bloqueia WebSocket.
    private bool _isRealTime;

    // Resultado do ÚLTIMO "Sincronizar agora" (só do clique — o laço de fundo não mexe aqui). Null
    // até o primeiro clique: carimbo inventado na abertura não significaria nada.
    private DateTimeOffset? _lastSyncNowAt;
    private bool _lastSyncNowFailed;

    /// <param name="controller">Controlador do sync; <c>null</c> = sem nuvem (offline-first).</param>
    /// <param name="clock">
    /// Relógio do carimbo. Injetável só por causa do teste que prova que o carimbo MUDA a cada clique:
    /// com o relógio real, dois cliques no mesmo segundo dariam o mesmo texto e o teste seria
    /// intermitente. Mesmo desenho do <c>CredentialVault</c>.
    /// </param>
    public SyncStatusViewModel(ISyncController? controller = null, TimeProvider? clock = null)
    {
        _controller = controller;
        _clock = clock ?? TimeProvider.System;
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

    // ── Retorno do "Sincronizar agora" ───────────────────────────────────────────────────────
    //
    // POR QUE existe: o clique SEMPRE rodou o ciclo real (push+pull), mas o resultado era descartado
    // (o bool do ISyncController ia pro lixo e o catch engolia a exceção). Quando o indicador já
    // estava em "Sincronizado", o ciclo rodava, terminava "Sincronizado" e NADA mudava na tela —
    // indistinguível de "o botão não fez nada", que foi exatamente a dúvida do operador em campo.
    //
    // POR QUE um carimbo com a HORA e não uma mensagem que some sozinha: os dois resolvem o "não sei
    // se rodou", mas a mensagem efêmera falha justo com quem ela deveria atender — este é um console
    // de operação de rede, o operador clica em "Sincronizar agora" e volta a digitar no equipamento;
    // quando olha de novo para a barra, uma confirmação de 3 segundos já sumiu e ele fica na mesma
    // dúvida. O carimbo fica até o próximo clique, e como leva os SEGUNDOS ele muda a cada clique —
    // que é o sinal de que algo aconteceu mesmo quando o estado final é igual ao inicial. De quebra
    // não precisa de timer nem de volta pro Dispatcher (menos peça, menos risco de thread).
    //
    // POR QUE não é modal (MessageBox): o mesmo motivo do aviso de atualização — um diálogo que rouba
    // foco pode pipocar enquanto o operador digita num roteador em produção, e o Enter destinado ao
    // equipamento vira "OK" na caixa. O retorno ACENDE na barra; não interrompe ninguém.

    /// <summary>Já houve um "Sincronizar agora" nesta sessão? Controla a visibilidade do carimbo.</summary>
    public bool HasSyncOutcome => _lastSyncNowAt is not null;

    /// <summary>
    /// Carimbo do último ciclo forçado. Sucesso e falha usam a MESMA forma (texto + hora) de propósito:
    /// o que o operador precisa distinguir é "deu" x "não deu", e a hora é o que prova que o clique
    /// desta vez chegou a rodar.
    /// </summary>
    public string SyncOutcomeText
    {
        get
        {
            if (_lastSyncNowAt is not { } when)
            {
                return string.Empty;
            }

            // Hora com SEGUNDOS: sem eles, dois cliques dentro do mesmo minuto exibiriam o mesmo
            // texto e o carimbo voltaria a não sinalizar nada — o bug que este recurso conserta.
            string hora = when.ToString("HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR"));

            return _lastSyncNowFailed
                ? $"Não sincronizou às {hora}"
                : $"Última sincronização: {hora}";
        }
    }

    /// <summary>
    /// Detalhe do carimbo (tooltip). É aqui que mora o "o que fazer agora" na falha — fora de qualquer
    /// diálogo modal, que não pode roubar o teclado do operador.
    /// </summary>
    public string SyncOutcomeDetail
    {
        get
        {
            if (!HasSyncOutcome)
            {
                return string.Empty;
            }

            return _lastSyncNowFailed
                ? "O ciclo terminou sem sincronizar. Verifique a conexão e clique de novo."
                : "O último \"Sincronizar agora\" enviou e recebeu as alterações sem erro.";
        }
    }

    /// <summary>O último "Sincronizar agora" FALHOU? Pinta o carimbo de erro na barra.</summary>
    public bool SyncOutcomeFailed => _lastSyncNowFailed;

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

    // ── Canal de SEGREDOS (Fatia 1k) ─────────────────────────────────────────────────────
    //
    // Até aqui, `grep -rn "SecretChannel" src/RemoteOps.Desktop/` devolvia ZERO: o orquestrador
    // calculava Degraded e Failed com cuidado e NADA disso chegava à tela. A barra dizia
    // "Sincronizado" enquanto senhas eram puladas — e qualquer guarda que mande item suspeito para o
    // SecretSyncSkip (a de raiz divergente, por exemplo) estava apoiada numa rede que ninguém via.

    /// <summary>
    /// O canal de senhas está com pendência? Separado do <see cref="IsError"/> de propósito: as duas
    /// metades do ciclo falham por motivos diferentes e pedem ações diferentes do operador.
    /// </summary>
    public bool HasSecretChannelWarning =>
        _secretChannel is SecretChannelState.Degraded or SecretChannelState.Failed;

    /// <summary>Aviso curto da barra. Vazio quando não há o que avisar — aviso permanente ninguém lê.</summary>
    public string SecretChannelText => _secretChannel switch
    {
        SecretChannelState.Degraded => "senhas com pendência",
        SecretChannelState.Failed => "senhas não sincronizaram",
        _ => string.Empty,
    };

    /// <summary>
    /// O detalhe acionável. Os dois estados pedem coisas diferentes: no degradado o acervo passou e
    /// ITENS ficaram para trás (o equipamento pode estar na tela com a senha faltando); no falho
    /// NENHUMA senha se moveu, e o operador precisa saber que o que ele vê é só metadado.
    /// </summary>
    public string SecretChannelDetail => _secretChannel switch
    {
        SecretChannelState.Degraded =>
            "Algumas senhas não subiram ou não desceram neste ciclo. Os equipamentos aparecem "
            + "normalmente, mas as senhas que faltaram só chegam no próximo ciclo bem-sucedido.",
        SecretChannelState.Failed =>
            "Nenhuma senha subiu ou desceu neste ciclo. Os equipamentos podem estar atualizados, "
            + "mas as senhas não — confira a conexão e sincronize de novo antes de contar com elas.",
        _ => string.Empty,
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
        _secretChannel = status.SecretChannel;
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

        // Começa em "não deu": qualquer caminho que não chegue ao fim do ciclo (exceção inclusive)
        // tem de virar aviso de falha. O padrão otimista é o que produzia o silêncio de antes.
        bool ok = false;
        try
        {
            // O bool É o resultado do ciclo (o orquestrador não relança — offline-first, ADR-002).
            // Descartá-lo era o bug: sem ele a falha ficava invisível e o sucesso, mudo.
            ok = await controller.SyncNowAsync();
        }
        catch
        {
            // O estado de erro chega pelo StatusChanged do orquestrador (→ Apply). Não estoura pra UI:
            // o botão volta a ficar clicável no finally pra o operador tentar de novo. A diferença
            // agora é que a falha também vira carimbo — antes o catch engolia e ninguém ficava sabendo.
        }
        finally
        {
            // ANTES do SetBusy(false) de propósito: quem observa "parou de sincronizar" (a barra e os
            // testes) já encontra o carimbo do ciclo pronto, sem um quadro intermediário mentindo.
            RecordOutcome(ok);
            SetBusy(false);
        }
    }

    private void RecordOutcome(bool ok)
    {
        _lastSyncNowAt = _clock.GetLocalNow();
        _lastSyncNowFailed = !ok;

        // Todas DERIVADAS dos dois campos acima: sem os raises explícitos o carimbo existiria só no
        // objeto e a barra continuaria muda — exatamente o sintoma que este recurso conserta.
        RaisePropertyChanged(nameof(HasSyncOutcome));
        RaisePropertyChanged(nameof(SyncOutcomeText));
        RaisePropertyChanged(nameof(SyncOutcomeDetail));
        RaisePropertyChanged(nameof(SyncOutcomeFailed));
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
        RaisePropertyChanged(nameof(HasSecretChannelWarning));
        RaisePropertyChanged(nameof(SecretChannelText));
        RaisePropertyChanged(nameof(SecretChannelDetail));
        SyncNowCommand.RaiseCanExecuteChanged();
    }
}
