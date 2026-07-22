using System;

using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// O aviso da <b>fila parada no cofre que não está aberto</b>.
///
/// <para>Com um banco por escopo (1j), editar no cofre pessoal e depois abrir no time deixa aquelas
/// edições esperando — e a barra continua dizendo "Sincronizado", porque o sync desta sessão
/// realmente terminou. O operador acha que subiu. Esta VM existe para que o aviso diga as duas coisas
/// que ele precisa: <b>quantos</b> itens e <b>o que fazer</b>.</para>
/// </summary>
public sealed class OtherVaultOutboxViewModelTests
{
    /// <summary>
    /// Nada parado, nada a verificar: <b>silêncio</b>. Aviso permanente é aviso que ninguém lê — e a
    /// maioria da frota tem um cofre só.
    /// </summary>
    [Fact]
    public void SemPendencia_NaoAvisaNada()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 0, pendingTeam: 0, checkFailed: false);

        Assert.False(vm.HasNotice);
        Assert.Equal(string.Empty, vm.Text);
        Assert.Equal(string.Empty, vm.Detail);
    }

    /// <summary>
    /// <b>O caso do operador.</b> Ele editou no cofre pessoal e está no time: o aviso diz QUANTOS e
    /// diz que o conserto é abrir o RemoteOps naquele cofre. Sem a segunda metade, o aviso viraria
    /// mais um número inexplicável na barra.
    /// </summary>
    [Fact]
    public void PendenciaNoCofrePESSOAL_DizQuantos_EOQueFazer()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 12, pendingTeam: 0, checkFailed: false);

        Assert.True(vm.HasNotice);
        Assert.Contains("12", vm.Text, StringComparison.Ordinal);
        Assert.Contains("cofre pessoal", vm.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("abr", vm.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cofre pessoal", vm.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nada foi perdido", vm.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Uma só: singular. Plural forçado ("1 alterações") é o tipo de detalhe que faz o
    /// operador desconfiar do número — e desconfiar do número é ignorar o aviso.</summary>
    [Fact]
    public void UmaSoAlteracao_FalaNoSINGULAR()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 1, pendingTeam: 0, checkFailed: false);

        Assert.Contains("1 alteração parada", vm.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alterações", vm.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O caminho inverso: a sessão é a pessoal e o que ficou parado é o do TIME.</summary>
    [Fact]
    public void PendenciaNoCofreDoTIME_NomeiaOCofreCERTO()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 0, pendingTeam: 4, checkFailed: false);

        Assert.True(vm.HasNotice);
        Assert.Contains("4", vm.Text, StringComparison.Ordinal);
        Assert.Contains("cofre do time", vm.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cofre pessoal", vm.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Os dois lados parados (o operador com dois times, por exemplo): soma e fala no plural.</summary>
    [Fact]
    public void PendenciaNosDOIS_SomaEFalaDosOutrosCofres()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 2, pendingTeam: 3, checkFailed: false);

        Assert.True(vm.HasNotice);
        Assert.Contains("5", vm.Text, StringComparison.Ordinal);
        Assert.Contains("outros cofres", vm.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>"Não deu para conferir" APARECE.</b> Um cofre que não pôde ser lido não pode virar "está
    /// tudo sincronizado": é exatamente assim que erro vira estado vazio nesta base.
    /// </summary>
    [Fact]
    public void NaoDeuParaConferir_APARECE_EmVezDeVirarTudoCerto()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 0, pendingTeam: 0, checkFailed: true);

        Assert.True(vm.HasNotice);
        Assert.NotEqual(string.Empty, vm.Text);
        Assert.Contains("verific", vm.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("abr", vm.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Contagem <b>e</b> escopo ilegível ao mesmo tempo: o número vira um PISO, e o texto diz isso.
    /// Afirmar "12" quando um cofre nem foi lido seria uma precisão que o app não tem.
    /// </summary>
    [Fact]
    public void ContagemComEscopoIlegivel_AvisaQuePodeHaverMAIS()
    {
        var vm = new OtherVaultOutboxViewModel();
        vm.Apply(pendingPersonal: 12, pendingTeam: 0, checkFailed: true);

        Assert.True(vm.HasNotice);
        Assert.Contains("12", vm.Text, StringComparison.Ordinal);
        Assert.Contains("mais", vm.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tudo aqui é DERIVADO dos três campos: sem o raise explícito, o aviso nasceria certo e
    /// envelheceria errado na tela — que é o pior dos dois mundos (lição do <c>VaultBadgeViewModel</c>).
    /// </summary>
    [Fact]
    public void Apply_NOTIFICA_ATela()
    {
        var vm = new OtherVaultOutboxViewModel();
        var mudou = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => mudou.Add(e.PropertyName ?? string.Empty);

        vm.Apply(pendingPersonal: 3, pendingTeam: 0, checkFailed: false);

        Assert.Contains(nameof(OtherVaultOutboxViewModel.HasNotice), mudou);
        Assert.Contains(nameof(OtherVaultOutboxViewModel.Text), mudou);
        Assert.Contains(nameof(OtherVaultOutboxViewModel.Detail), mudou);
    }
}
