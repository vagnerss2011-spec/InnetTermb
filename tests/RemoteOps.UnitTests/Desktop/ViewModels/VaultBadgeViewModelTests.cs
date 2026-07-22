using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;

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
    /// <b>O estado que precisa gritar.</b> O workspace escolhido é de TIME, mas o cofre que o app
    /// abre é o pessoal (o compartilhado é a Fatia 2). Sem esse aviso, tudo o que for cadastrado
    /// aqui chega ao colega com a senha ilegível — e nada na tela denuncia.
    /// </summary>
    [Fact]
    public void WorkspaceDeTime_ComCofrePessoalAtivo_AVISA_ComTextoAcionavel()
    {
        VaultBadgeViewModel badge = New();
        badge.Apply(VaultScope.TeamPending);

        Assert.True(badge.IsWarning);
        Assert.Contains("PESSOAL", badge.Detail, StringComparison.Ordinal);
        Assert.Contains("senha", badge.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultBadgeViewModel.TeamVaultNotActiveWarning, badge.Detail);

        // O rótulo curto (o que cabe na barra) também precisa carregar o alerta: um operador que só
        // bate o olho na barra não abre tooltip nenhum.
        Assert.Contains("pessoal", badge.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time", badge.Label, StringComparison.OrdinalIgnoreCase);
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

        await badge.RefreshAsync(_ => Task.FromResult(true));

        Assert.Equal(VaultScope.TeamPending, badge.Scope);
        Assert.True(badge.IsWarning);
    }

    [Fact]
    public async Task Sondagem_DizendoQueNaoETime_LevaAoCofrePessoal()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(_ => Task.FromResult(false));

        Assert.Equal(VaultScope.Personal, badge.Scope);
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

    /// <summary>Sem sondagem (modo local puro) o estado permanece o de sempre — nada muda.</summary>
    [Fact]
    public async Task SemSondagem_ContinuaLocal()
    {
        VaultBadgeViewModel badge = New();

        await badge.RefreshAsync(isTeamWorkspace: null);

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
                    return Task.FromResult(true);
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
