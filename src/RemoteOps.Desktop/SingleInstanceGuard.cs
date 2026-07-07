using System.Threading;

namespace RemoteOps.Desktop;

/// <summary>
/// Garante uma única instância do RemoteOps por usuário. A segunda instância sinaliza a primeira
/// (para ela trazer a janela para frente) e encerra ANTES de abrir o banco — evitando a disputa do
/// SqlCipher local (<c>sync-local.db</c>, <c>Pooling=False</c>) que dava erros confusos em campo
/// quando o ícone era clicado duas vezes. Sem dependência de UI aqui (testável); a ativação da
/// janela é injetada como callback. Nomes de mutex/evento são parametrizáveis para os testes não
/// colidirem num nome global fixo.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // Escopo Local\ = por sessão de usuário; não colide entre contas nem exige privilégio.
    private const string DefaultMutexName = @"Local\RemoteOps.Desktop.SingleInstance";
    private const string DefaultSignalName = @"Local\RemoteOps.Desktop.Activate";

    private readonly Mutex _mutex;
    private readonly string _signalName;
    private EventWaitHandle? _signal;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string? mutexName = null, string? signalName = null)
    {
        _signalName = signalName ?? DefaultSignalName;
        _mutex = new Mutex(initiallyOwned: true, mutexName ?? DefaultMutexName, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>Chamado só pela SEGUNDA instância: acorda a primeira. true se conseguiu sinalizar.</summary>
    public bool SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(_signalName, out EventWaitHandle? handle))
            {
                using (handle)
                {
                    handle.Set();
                }
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // A primeira instância ainda não registrou o listener (corrida de startup) — sem ativação.
        }
        return false;
    }

    /// <summary>
    /// Chamado só pela PRIMEIRA instância: registra o listener. <paramref name="onActivate"/> roda
    /// numa thread de fundo sempre que outra instância tenta abrir — o chamador deve fazer o marshal
    /// para a UI thread (Dispatcher).
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        _signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _signalName);
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_signal.WaitOne(500) && !_disposed)
                    {
                        onActivate();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "RemoteOps-SingleInstanceListener",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _signal?.Set(); // acorda o listener para ele sair do WaitOne e encerrar
        }
        catch (ObjectDisposedException)
        {
        }

        _signal?.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Já liberado / thread diferente — o SO libera no fim do processo de qualquer forma.
            }
        }

        _mutex.Dispose();
    }
}
