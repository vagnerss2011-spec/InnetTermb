using System;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Agenda uma ação com DEBOUNCE de cauda (trailing): cada <see cref="Signal"/> reinicia a janela e a
/// ação só roda quando os sinais PARAM por <c>window</c>. Serve à Fase 2 do cloud sync: um pull grande
/// chega em vários lotes (páginas do changelog + ticks concorrentes de hint/intervalo), e sem debounce
/// cada lote dispararia uma reconciliação da lista de hosts — N recargas onde uma basta (item 3).
///
/// <para><b>Thread-safe.</b> <see cref="Signal"/> pode vir de qualquer thread (o evento
/// <c>ChangesApplied</c> é levantado na thread de fundo do sync). A ação é invocada no callback de um
/// <see cref="Timer"/> (thread de pool); quem precisar de afinidade de UI marshala pro Dispatcher
/// DENTRO da ação. Exceção da ação nunca derruba o processo (é engolida — a ação roda fire-and-forget).</para>
/// </summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Func<Task> _action;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    public DebouncedAction(TimeSpan window, Func<Task> action)
    {
        _window = window;
        _action = action ?? throw new ArgumentNullException(nameof(action));
        // Timer parado; cada Signal o (re)arma pra disparar uma vez após a janela.
        _timer = new Timer(_ => Fire(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Pede a execução da ação daqui a uma janela; um novo Signal antes disso reinicia a contagem.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer.Change(_window, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Fora do lock: a ação pode demorar (reconciliação lê o store). Fire-and-forget com guarda —
        // uma recarga que falha não pode virar exceção não observada e derrubar o app.
        _ = RunGuardedAsync();
    }

    private async Task RunGuardedAsync()
    {
        try
        {
            await _action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best-effort: a próxima rodada de sync tenta de novo.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }
}
