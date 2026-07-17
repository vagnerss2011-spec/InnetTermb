using System;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

/// <summary>
/// Fase 2, item 3: um pull grande chega em vários lotes; sem debounce cada lote dispararia uma
/// reconciliação da lista. <see cref="DebouncedAction"/> agrupa a rajada numa execução só. Testes de
/// tempo com margens folgadas (janela pequena, espera longa) — o padrão já usado na suíte.
/// </summary>
public sealed class DebouncedActionTests
{
    [Fact]
    public async Task Multiple_Signals_In_Window_Coalesce_Into_One_Run()
    {
        int runs = 0;
        using var debounced = new DebouncedAction(
            TimeSpan.FromMilliseconds(60),
            () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        // Rajada sem espera: os 8 sinais caem dentro da mesma janela (o laço é ordens de grandeza
        // mais rápido que 60ms), então só a ÚLTIMA contagem vale — uma execução.
        for (int i = 0; i < 8; i++)
        {
            debounced.Signal();
        }

        await Task.Delay(250);

        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Signals_In_Separate_Windows_Run_Each_Time()
    {
        int runs = 0;
        using var debounced = new DebouncedAction(
            TimeSpan.FromMilliseconds(40),
            () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        debounced.Signal();
        await Task.Delay(150); // 1ª janela fecha e dispara
        debounced.Signal();
        await Task.Delay(150); // 2ª janela fecha e dispara

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task No_Signal_Means_No_Run()
    {
        int runs = 0;
        using var debounced = new DebouncedAction(
            TimeSpan.FromMilliseconds(30),
            () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        await Task.Delay(120);

        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Signal_After_Dispose_Is_Ignored()
    {
        int runs = 0;
        var debounced = new DebouncedAction(
            TimeSpan.FromMilliseconds(30),
            () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        debounced.Dispose();
        debounced.Signal();
        await Task.Delay(120);

        Assert.Equal(0, Volatile.Read(ref runs));
    }
}
