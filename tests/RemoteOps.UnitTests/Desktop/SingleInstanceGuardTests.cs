using System;
using System.Threading;
using RemoteOps.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Cobre a lógica de instância única (detecção + sinalização entre instâncias) usando mutex/evento
/// reais do SO, com nomes únicos por teste para não colidir entre execuções paralelas.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    private static (string Mutex, string Signal) UniqueNames()
    {
        string id = Guid.NewGuid().ToString("n");
        return ($@"Local\ro-test-mtx-{id}", $@"Local\ro-test-sig-{id}");
    }

    [Fact]
    public void FirstInstanceIsFirst_SecondIsNot()
    {
        var (m, s) = UniqueNames();
        using var first = new SingleInstanceGuard(m, s);
        using var second = new SingleInstanceGuard(m, s);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void SignalExistingInstance_InvokesFirstInstanceCallback()
    {
        var (m, s) = UniqueNames();
        using var first = new SingleInstanceGuard(m, s);
        using var activated = new ManualResetEventSlim(false);
        first.ListenForActivation(() => activated.Set());

        using var second = new SingleInstanceGuard(m, s);

        Assert.False(second.IsFirstInstance);
        Assert.True(second.SignalExistingInstance());
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)), "a primeira instância deveria ter sido ativada");
    }

    // ── Handshake de ativação ────────────────────────────────────────────────────────────────
    //
    // O caso que derrubou o operador em campo: a primeira instância continua VIVA (segurando o mutex)
    // mas com a UI thread pendurada, então o sinal é entregue e nada aparece na tela. Sinalizar com
    // sucesso NÃO prova que a janela subiu — só a confirmação de volta prova.

    [Fact]
    public void SignalAndWait_Confirms_When_First_Instance_Responds()
    {
        var (m, s) = UniqueNames();
        using var first = new SingleInstanceGuard(m, s);
        first.ListenForActivation(() => { /* ativação instantânea, como num app saudável */ });

        using var second = new SingleInstanceGuard(m, s);

        Assert.True(second.SignalExistingInstanceAndWait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalAndWait_Fails_When_First_Instance_Is_Wedged()
    {
        var (m, s) = UniqueNames();
        using var first = new SingleInstanceGuard(m, s);
        using var release = new ManualResetEventSlim(false);

        // Simula a UI thread travada: o callback de ativação não retorna dentro do teto.
        first.ListenForActivation(() => release.Wait(TimeSpan.FromSeconds(30)));

        using var second = new SingleInstanceGuard(m, s);

        // Sem confirmação: quem chama precisa AVISAR o usuário, nunca encerrar em silêncio.
        Assert.False(second.SignalExistingInstanceAndWait(TimeSpan.FromMilliseconds(400)));

        release.Set(); // libera a thread de fundo do teste
    }

    [Fact]
    public void SignalAndWait_Fails_When_There_Is_No_Listener()
    {
        var (m, s) = UniqueNames();
        using var first = new SingleInstanceGuard(m, s); // segura o mutex e NUNCA escuta
        using var second = new SingleInstanceGuard(m, s);

        Assert.False(second.SignalExistingInstanceAndWait(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void AfterFirstDisposed_NextInstanceBecomesFirst()
    {
        var (m, s) = UniqueNames();
        var first = new SingleInstanceGuard(m, s);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using var next = new SingleInstanceGuard(m, s);
        Assert.True(next.IsFirstInstance);
    }
}
