using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O indicador de cofre (Fatia 1e). O erro caro desta fatia não é a criptografia: é o operador
/// cadastrar o equipamento de um cliente achando que está no cofre do time e só descobrir semanas
/// depois, quando o colega diz que não vê nada. Estes testes fixam que o indicador diz a VERDADE em
/// cada estado — inclusive no estado incômodo, em que o workspace é de time mas o cofre que o app
/// abre continua sendo o pessoal.
/// </summary>
public sealed class VaultBadgeViewModelTests
{
    private static VaultBadgeViewModel New() => new();

    [Fact]
    public void SemConta_DizQueOCofreEPessoalENaoSaiDoPC()
    {
        VaultBadgeViewModel badge = New();

        Assert.Equal(VaultScope.LocalOnly, badge.Scope);
        Assert.Contains("pessoal", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.False(badge.IsWarning);
        Assert.NotEmpty(badge.Detail);
    }

    /// <summary>O título da janela leva o cofre — é o que continua visível com uma sessão SSH aberta.</summary>
    [Fact]
    public void TituloDaJanela_LevaOCofre_ENaoSoONomeDoApp()
    {
        VaultBadgeViewModel badge = New();
        badge.Apply(VaultScope.Personal);

        Assert.StartsWith("RemoteOps", badge.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(badge.Label, badge.WindowTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>O estado que precisa gritar (reescrito na Fatia 1i).</b> O cofre do TIME está ativo e a
    /// chave AINDA NÃO chegou neste computador: os equipamentos aparecem e nenhuma senha do time
    /// abre ou é gravada. O texto antigo ("o cofre compartilhado ainda não abre nesta versão") virou
    /// MENTIRA quando o cofre do time passou a ser o cofre ativo — e um aviso que mente é pior que
    /// aviso nenhum, porque o operador aprende a ignorá-lo.
    /// </summary>
    [Fact]
    public void CofreDoTimeSemAChave_AVISA_ComTextoAcionavel()
    {
        VaultBadgeViewModel badge = New();
        badge.Apply(VaultScope.TeamPending);

        Assert.True(badge.IsWarning);
        Assert.Contains("chave", badge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("senha", badge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultBadgeViewModel.TeamVaultNotActiveWarning, badge.Detail);

        // O rótulo curto (o que cabe na barra) também carrega o alerta: quem só bate o olho na barra
        // não abre tooltip nenhum.
        Assert.Contains("time", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chave", badge.Label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Cofre do time ATIVO: o indicador para de dizer "pessoal".</b> Era esta a limitação que o
    /// 1e registrou honestamente na tela; agora ela acabou, e o texto tem de acompanhar. E NÃO é
    /// alerta: um aviso permanente é um aviso que ninguém mais lê.
    /// </summary>
    [Fact]
    public void CofreDoTimeAtivo_DizQueEDoTime_ESemAlarme()
    {
        VaultBadgeViewModel badge = New();
        badge.Apply(VaultScope.Team);

        Assert.False(badge.IsWarning);
        Assert.Contains("TIME", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pessoal", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultBadgeViewModel.TeamVaultActiveDetail, badge.Detail);
        Assert.Contains("TIME", badge.WindowTitle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cofre pessoal com conta: sem alarme, e ainda assim dizendo qual cofre é.</summary>
    [Fact]
    public void CofrePessoalComConta_NaoAlarma_MasSeIdentifica()
    {
        VaultBadgeViewModel badge = New();
        badge.Apply(VaultScope.Personal);

        Assert.False(badge.IsWarning);
        Assert.False(badge.IsUnconfirmed);
        Assert.Contains("pessoal", badge.Label, StringComparison.OrdinalIgnoreCase);
    }

    // ── A sondagem (é workspace de time?) ────────────────────────────────────────────────

    [Fact]
    public async Task Sondagem_DizendoQueETime_LevaAoEstadoDeAviso()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(_ => Task.FromResult(WorkspaceKindFact.Team));

        Assert.Equal(VaultScope.TeamPending, badge.Scope);
        Assert.True(badge.IsWarning);
    }

    [Fact]
    public async Task Sondagem_DizendoQueNaoETime_LevaAoCofrePessoal()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(_ => Task.FromResult(WorkspaceKindFact.Personal));

        Assert.Equal(VaultScope.Personal, badge.Scope);
    }

    /// <summary>
    /// ⚠️ <b>"Não sei" tem estado PRÓPRIO na barra.</b> A sondagem responde por um 404 de
    /// <c>GET /workspaces/{id}/key</c>, que significa "a SUA CONTA não guarda embrulho aqui" — e é
    /// indistinguível de um 404 de infraestrutura (proxy sem a rota, backend velho). Escrever "cofre
    /// pessoal" com essa dúvida é a MESMA mentira que o caminho de exceção abaixo já não conta, só
    /// que sem nem uma exceção para justificá-la: o operador cadastraria o cliente sem ver o aviso.
    /// </summary>
    [Fact]
    public async Task Sondagem_QueNaoSABE_NaoVira_CofrePessoal_EFica_NaoConfirmado()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(_ => Task.FromResult(WorkspaceKindFact.Unknown));

        Assert.Equal(VaultScope.Unconfirmed, badge.Scope);
        Assert.True(badge.IsUnconfirmed);
        Assert.NotEqual(VaultScope.Personal, badge.Scope);
    }

    /// <summary>
    /// <b>A regra da casa.</b> Servidor fora do ar não pode virar "cofre pessoal" com toda a
    /// confiança: seria uma afirmação que o app não tem como sustentar, justo sobre o assunto em que
    /// errar custa caro. Fica "não confirmado", visível.
    /// </summary>
    [Fact]
    public async Task Sondagem_QueFALHA_NaoVira_CofrePessoal()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(_ => throw new HttpRequestException("rede fora (teste)"));

        Assert.Equal(VaultScope.Unconfirmed, badge.Scope);
        Assert.True(badge.IsUnconfirmed);
        Assert.NotEqual(VaultScope.Personal, badge.Scope);
        Assert.Contains("confirm", badge.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>A sondagem NUNCA rebaixa o cofre do time ATIVO.</b> A reavaliação roda toda vez que a
    /// janela de convite fecha — inclusive numa sessão que abriu o cofre do time COM a chave. A
    /// sondagem responde "é de time" sem saber da chave, e o mapeamento cru (é de time →
    /// TeamPending) faria a barra e o título gritarem "a chave ainda não chegou / nenhuma senha é
    /// gravada aqui" com o cofre funcionando perfeitamente. Alarme falso ensina o operador a
    /// ignorar o ÚNICO aviso que importa de verdade.
    /// </summary>
    [Fact]
    public async Task Sondagem_NaoRebaixaOCofreDoTimeAtivo_ParaPendente()
    {
        VaultBadgeViewModel badge = New();
        badge.ApplyFromSession(RemoteOps.Desktop.Account.SessionVaultKind.Team);

        await badge.RefreshAsync(_ => Task.FromResult(WorkspaceKindFact.Team));

        Assert.Equal(VaultScope.Team, badge.Scope);
        Assert.False(badge.IsWarning);
    }

    /// <summary>
    /// Nem para "não confirmado": a resposta do boot saiu do DISCO (a chave está aqui) e vale
    /// offline. Uma falha de sondagem não desconfirma o que o disco afirmou — e o texto de
    /// Unconfirmed ("o cofre aberto aqui é o PESSOAL") seria mentira numa sessão de time.
    /// </summary>
    [Fact]
    public async Task SondagemQueFalha_NaoRebaixaOCofreDoTimeAtivo_ParaNaoConfirmado()
    {
        VaultBadgeViewModel badge = New();
        badge.ApplyFromSession(RemoteOps.Desktop.Account.SessionVaultKind.Team);

        await badge.RefreshAsync(_ => throw new HttpRequestException("rede fora (teste)"));

        Assert.Equal(VaultScope.Team, badge.Scope);
        Assert.False(badge.IsUnconfirmed);
    }

    /// <summary>Sem sondagem (modo local puro) o estado permanece o de sempre — nada muda.</summary>
    [Fact]
    public async Task SemSondagem_ContinuaLocal()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(probeWorkspaceKind: null);

        Assert.Equal(VaultScope.LocalOnly, badge.Scope);
    }

    /// <summary>
    /// Cancelamento é o app fechando, não falha de rede — não pode ser carimbado como "não
    /// confirmado" nem ficar preso num estado antigo. Sobe, como em todo o resto da base.
    /// </summary>
    [Fact]
    public async Task Cancelamento_Sobe_EmVezDeVirarNaoConfirmado()
    {
        VaultBadgeViewModel badge = New();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // A sondagem real (HttpClient por baixo) honra o token; a fake precisa fazer o mesmo, senão
        // o teste não exerce nada — o alvo aqui é a ORDEM dos catch dentro do RefreshAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => badge.RefreshAsync(
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(WorkspaceKindFact.Team);
                },
                cts.Token));

        Assert.Equal(VaultScope.LocalOnly, badge.Scope);
    }

    /// <summary>Mudar de estado precisa NOTIFICAR — senão o indicador nasce certo e envelhece errado.</summary>
    [Fact]
    public void MudarDeEstado_Notifica_OsCamposQueAUITemNaTela()
    {
        VaultBadgeViewModel badge = New();
        var mudou = new System.Collections.Generic.List<string>();
        badge.PropertyChanged += (_, e) => mudou.Add(e.PropertyName ?? string.Empty);

        badge.Apply(VaultScope.TeamPending);

        Assert.Contains(nameof(VaultBadgeViewModel.Label), mudou);
        Assert.Contains(nameof(VaultBadgeViewModel.Detail), mudou);
        Assert.Contains(nameof(VaultBadgeViewModel.WindowTitle), mudou);
        Assert.Contains(nameof(VaultBadgeViewModel.IsWarning), mudou);
    }
}
