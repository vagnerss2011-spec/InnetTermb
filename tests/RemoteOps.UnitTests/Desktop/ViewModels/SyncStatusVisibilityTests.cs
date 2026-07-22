using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O canal de SEGREDOS deixa de ser mudo.
///
/// <para><b>O achado que motiva este arquivo:</b> <c>grep -rn "SecretChannel" src/RemoteOps.Desktop/</c>
/// devolvia ZERO. O orquestrador calculava <see cref="SecretChannelState.Degraded"/> e
/// <see cref="SecretChannelState.Failed"/> com cuidado, e nenhum deles chegava à tela — a barra dizia
/// "Sincronizado" enquanto senhas eram puladas. Qualquer desenho que use o <c>SecretSyncSkip</c> como
/// rede de segurança (e a guarda de raiz divergente usa) estava apoiado numa rede que ninguém
/// enxergava.</para>
/// </summary>
public sealed class SyncStatusVisibilityTests
{
    private static SyncStatusViewModel Vm() => new();

    /// <summary>
    /// <b>Canal degradado APARECE.</b> O ciclo terminou, os metadados passaram, e ITENS de senha
    /// foram pulados: dizer só "Sincronizado" seria a mentira mais cara da barra — o operador
    /// acharia que a senha do cliente está lá.
    /// </summary>
    [Fact]
    public void CanalDeSegredosDegradado_APARECE_NaBarra()
    {
        SyncStatusViewModel vm = Vm();

        vm.Apply(new SyncStatus(SyncState.Synced, ConflictCount: 0, SecretChannelState.Degraded));

        Assert.True(vm.HasSecretChannelWarning);
        Assert.Contains("senha", vm.SecretChannelText, System.StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(vm.SecretChannelDetail);
    }

    /// <summary>
    /// Canal inteiro no chão: os metadados podem ter passado (o host aparece), mas NENHUMA senha
    /// subiu ou desceu. O texto precisa ser diferente do degradado — as ações que o operador toma
    /// são diferentes.
    /// </summary>
    [Fact]
    public void CanalDeSegredosNoChao_APARECE_ComTextoProprio()
    {
        SyncStatusViewModel vm = Vm();

        vm.Apply(new SyncStatus(SyncState.Synced, ConflictCount: 0, SecretChannelState.Failed));

        Assert.True(vm.HasSecretChannelWarning);
        Assert.NotEqual(
            TextoDe(SecretChannelState.Degraded), vm.SecretChannelText);
    }

    /// <summary>
    /// Canal saudável (ou inexistente) não acende nada: um aviso permanente é um aviso que ninguém
    /// mais lê, e a barra voltaria a ser decorativa por outro caminho.
    /// </summary>
    [Theory]
    [InlineData(SecretChannelState.Idle)]
    [InlineData(SecretChannelState.Healthy)]
    public void CanalSaudavelOuInexistente_NaoAcendeNada(SecretChannelState estado)
    {
        SyncStatusViewModel vm = Vm();

        vm.Apply(new SyncStatus(SyncState.Synced, ConflictCount: 0, estado));

        Assert.False(vm.HasSecretChannelWarning);
        Assert.Empty(vm.SecretChannelText);
    }

    private static string TextoDe(SecretChannelState estado)
    {
        SyncStatusViewModel vm = Vm();
        vm.Apply(new SyncStatus(SyncState.Synced, ConflictCount: 0, estado));
        return vm.SecretChannelText;
    }
}
