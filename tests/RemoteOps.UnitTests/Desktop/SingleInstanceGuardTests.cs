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
