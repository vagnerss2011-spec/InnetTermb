using System;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// ⚠️ <b>O caminho de produção até a tela de escolha do cofre — visto da tela.</b>
///
/// <para>Até aqui o operador clicava em "Criar time…", quatro mensagens diferentes mandavam
/// <i>"feche e abra o RemoteOps e escolha o time ao entrar"</i>, ele fechava, abria e o app entrava
/// direto no cofre pessoal. Nada mudava — sem erro, sem log e sem caminho, porque
/// <c>AccountSyncCoordinator.LogoutAsync</c> (o único código que apaga o cache da AMK) não tinha um
/// único chamador de produção.</para>
///
/// <para>Estes testes fixam as três coisas que fecham isso: o botão existe e faz o que promete; ele
/// <b>pergunta antes</b>, dizendo o que ficaria na fila; e as quatro mensagens apontam para ele em
/// vez de mentir.</para>
/// </summary>
public sealed class SettingsViewModelVaultSwitchTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();

        public AppSettings Load() => _current;

        public void Save(AppSettings settings) => _current = settings;
    }

    /// <summary>O que o app sabe fazer ao trocar de cofre, sem conta, rede nem SQLCipher.</summary>
    private sealed class FakeVaultSwitch : IVaultSwitch
    {
        internal VaultSwitchBacklog Backlog { get; set; } = VaultSwitchBacklog.Empty;

        internal int SignOuts { get; private set; }

        internal Exception? ThrowOnSignOut { get; set; }

        public Task<VaultSwitchBacklog> ReadBacklogAsync(CancellationToken ct = default)
            => Task.FromResult(Backlog);

        public Task SignOutAsync(CancellationToken ct = default)
        {
            if (ThrowOnSignOut is not null)
            {
                return Task.FromException(ThrowOnSignOut);
            }

            SignOuts++;
            return Task.CompletedTask;
        }
    }

    private static SettingsViewModel BuildVm(FakeVaultSwitch? vaultSwitch)
        => new(new FakeSettingsStore(), vaultSwitch: vaultSwitch);

    // ── (a) o botão existe e é alcançável ────────────────────────────────────────────────────

    /// <summary>
    /// Com conta ativa o botão existe. Sem ele não há caminho nenhum até a tela de escolha do cofre:
    /// o app entra pelo cache e nunca pergunta.
    /// </summary>
    [Fact]
    public void ComConta_OBotaoDeTrocarDeCofre_EXISTE_EEstaHabilitado()
    {
        SettingsViewModel vm = BuildVm(new FakeVaultSwitch());

        Assert.True(vm.CanSwitchVault);
        Assert.True(vm.SwitchVaultCommand.CanExecute(null));
        Assert.Equal(VaultSwitchText.ButtonLabel, vm.SwitchVaultButtonText);
    }

    /// <summary>Sem conta (modo local) não há de onde sair — e um botão que só recusa é ruído.</summary>
    [Fact]
    public void SemConta_NaoHaDeOndeSair()
    {
        SettingsViewModel vm = BuildVm(null);

        Assert.False(vm.CanSwitchVault);
        Assert.False(vm.SwitchVaultCommand.CanExecute(null));
    }

    // ── (b) pergunta ANTES, e diz o que ficaria para trás ────────────────────────────────────

    /// <summary>
    /// ⚠️ O clique <b>não</b> troca nada: abre a confirmação e MEDE a fila desta sessão. Sair da conta
    /// por um clique acidental, com trabalho na fila, seria a pior surpresa possível para quem tem
    /// centenas de equipamentos.
    /// </summary>
    [Fact]
    public async Task OCliqueNaoTROCA_NADA_Ele_PERGUNTA_EMedeAFila()
    {
        var alvo = new FakeVaultSwitch { Backlog = new VaultSwitchBacklog(12, CheckFailed: false) };
        SettingsViewModel vm = BuildVm(alvo);

        await vm.OpenSwitchVaultAsync();

        Assert.True(vm.IsSwitchVaultConfirmVisible);
        Assert.Equal(0, alvo.SignOuts);
        Assert.True(vm.HasSwitchVaultBacklog);
        Assert.Contains("12", vm.SwitchVaultBacklogText, StringComparison.Ordinal);
    }

    /// <summary>Fila vazia (medida): sem aviso nenhum — aviso permanente é aviso que ninguém lê.</summary>
    [Fact]
    public async Task FilaVAZIA_NaoInventaAviso()
    {
        SettingsViewModel vm = BuildVm(new FakeVaultSwitch());

        await vm.OpenSwitchVaultAsync();

        Assert.True(vm.IsSwitchVaultConfirmVisible);
        Assert.False(vm.HasSwitchVaultBacklog);
    }

    /// <summary>
    /// ⚠️ "Não deu para conferir" APARECE. Afirmar "nada pendente" sobre um banco que ninguém
    /// conseguiu ler é o erro virando estado vazio — no exato instante em que o operador decide.
    /// </summary>
    [Fact]
    public async Task NaoVerificado_APARECE_EmVezDeSilencio()
    {
        SettingsViewModel vm = BuildVm(
            new FakeVaultSwitch { Backlog = new VaultSwitchBacklog(0, CheckFailed: true) });

        await vm.OpenSwitchVaultAsync();

        Assert.True(vm.HasSwitchVaultBacklog);
        Assert.Contains("não foi possível", vm.SwitchVaultBacklogText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cancelar fecha a confirmação e NÃO sai da conta.</summary>
    [Fact]
    public async Task Cancelar_NaoSaiDaConta()
    {
        var alvo = new FakeVaultSwitch();
        SettingsViewModel vm = BuildVm(alvo);
        await vm.OpenSwitchVaultAsync();

        vm.CancelSwitchVaultCommand.Execute(null);

        Assert.False(vm.IsSwitchVaultConfirmVisible);
        Assert.Equal(0, alvo.SignOuts);
    }

    // ── (c) confirmar sai da conta E pede o reinício ─────────────────────────────────────────

    /// <summary>
    /// <b>O caminho inteiro.</b> Confirmar chama o serviço (que em produção drena e chama o
    /// <c>LogoutAsync</c>) e pede o reinício — é o reinício que faz o boot voltar a perguntar. Sair
    /// da conta sem reiniciar deixaria o app rodando sobre um cofre de que ele já saiu.
    /// </summary>
    [Fact]
    public async Task Confirmar_SAI_DaConta_EPedeOReinicio()
    {
        var alvo = new FakeVaultSwitch();
        SettingsViewModel vm = BuildVm(alvo);
        int reinicios = 0;
        vm.RestartRequested += (_, _) => reinicios++;

        await vm.OpenSwitchVaultAsync();
        await vm.SwitchVaultNowAsync();

        Assert.Equal(1, alvo.SignOuts);
        Assert.Equal(1, reinicios);
    }

    /// <summary>
    /// Falhar ao sair NÃO pode reiniciar assim mesmo: reiniciar com o cache da AMK intacto devolve o
    /// operador ao MESMO cofre, e ele acharia que o botão não faz nada — de volta ao beco sem saída.
    /// A falha vira texto na tela.
    /// </summary>
    [Fact]
    public async Task FalhandoAoSair_NaoReinicia_EDIZ_OQueAconteceu()
    {
        var alvo = new FakeVaultSwitch { ThrowOnSignOut = new InvalidOperationException("cofre travado") };
        SettingsViewModel vm = BuildVm(alvo);
        int reinicios = 0;
        vm.RestartRequested += (_, _) => reinicios++;

        await vm.OpenSwitchVaultAsync();
        await vm.SwitchVaultNowAsync();

        Assert.Equal(0, reinicios);
        Assert.NotEmpty(vm.SwitchVaultStatus);
    }

    // ── (d) as quatro mensagens apontam para o botão ─────────────────────────────────────────

    /// <summary>
    /// ⚠️ <b>Enquanto a frase disser "feche e abra", ela mente.</b> As quatro telas mandavam o
    /// operador fazer algo que não funcionava. Agora todas saem da MESMA constante — quatro cópias
    /// eram quatro bugs esperando divergir no primeiro ajuste de texto.
    /// </summary>
    [Theory]
    [InlineData(SettingsViewModel.PersonalSessionNotice)]
    [InlineData(TeamInviteViewModel.EmptyTeamWarning)]
    [InlineData(TeamInviteViewModel.TeamCreatedNotice)]
    [InlineData(TeamInviteViewModel.TeamCreatedInThisWindowNotice)]
    public void AsQuatroMensagens_APONTAM_ParaOBotao_EmVezDeMentir(string texto)
    {
        Assert.Contains(VaultSwitchText.HowToSwitch, texto, StringComparison.Ordinal);
        Assert.DoesNotContain("feche e abra", texto, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A frase única nomeia o botão e diz o que vai acontecer — reinício e senha. Omitir isso faria o
    /// operador clicar achando que é instantâneo e desconfiar do app quando a tela de login aparecesse.
    /// </summary>
    [Fact]
    public void AFraseUnica_NOMEIA_OBotao_EAvisaDoReinicio()
    {
        Assert.Contains(VaultSwitchText.ButtonLabel, VaultSwitchText.HowToSwitch, StringComparison.Ordinal);
        Assert.Contains("reinicia", VaultSwitchText.HowToSwitch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("senha", VaultSwitchText.HowToSwitch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Sua sessão expirou. Feche e abra o RemoteOps" era a MESMA mentira por outro caminho: com o
    /// cache da AMK em disco, reabrir reusa os tokens do cofre e não entra de novo em lugar nenhum.
    /// Três cópias do texto, três telas mentindo — agora uma constante só, apontando para o botão.
    /// </summary>
    [Fact]
    public void OTextoDeSessaoExpirada_TambemAPONTA_ParaOBotao()
    {
        Assert.Contains(
            VaultSwitchText.HowToSwitch, VaultSwitchText.SessionExpired, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Feche e abra", VaultSwitchText.SessionExpired, StringComparison.OrdinalIgnoreCase);
    }
}
