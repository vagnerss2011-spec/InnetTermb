namespace RemoteOps.Sync.Remote;

/// <summary>Estado do orquestrador de sync, refletido na UI (MainViewModel.SyncStatus).</summary>
public enum SyncState
{
    Offline,
    Syncing,
    Synced,
    Error,
}

/// <summary>
/// Saúde do canal PRÓPRIO dos segredos (<see cref="SecretSyncOrchestrator"/>), reportada SEPARADA do
/// changelog.
///
/// <para><b>Por que separada:</b> as duas metades do ciclo falham por motivos diferentes e pedem
/// ações diferentes. Colapsar as duas em <see cref="SyncState.Error"/> foi o que deixou o operador
/// com "Sincronizado" na tela enquanto NENHUMA senha subia: o changelog passava, o canal de segredos
/// morria, e o único sinal possível era um "Erro" genérico que nem sequer aparecia.</para>
/// </summary>
public enum SecretChannelState
{
    /// <summary>Sem canal de segredos (conta sem nuvem E2EE) ou ele ainda não rodou.</summary>
    Idle,

    /// <summary>O ciclo moveu os envelopes sem pular nada.</summary>
    Healthy,

    /// <summary>O canal rodou, mas ITENS foram pulados (envelope malformado). Os demais passaram.</summary>
    Degraded,

    /// <summary>O canal inteiro falhou (servidor fora, token). Os METADADOS podem estar bem.</summary>
    Failed,
}

/// <summary>Estado atual do sync + conflitos pendentes + saúde do canal de segredos.</summary>
public sealed record SyncStatus(
    SyncState State,
    int ConflictCount = 0,
    SecretChannelState SecretChannel = SecretChannelState.Idle);
