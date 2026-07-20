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
    private readonly string _ackName;
    private EventWaitHandle? _signal;
    private EventWaitHandle? _ack;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string? mutexName = null, string? signalName = null)
    {
        _signalName = signalName ?? DefaultSignalName;
        // Derivado do nome do sinal para os testes (que passam nomes únicos) não colidirem.
        _ackName = _signalName + ".ack";
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
    /// Sinaliza a primeira instância e ESPERA a confirmação de que ela realmente trouxe a janela para
    /// frente. Devolve <c>false</c> se não houve confirmação dentro de <paramref name="timeout"/>.
    ///
    /// <para>Por que a confirmação importa: entregar o sinal só prova que o processo existe, não que ele
    /// está SAUDÁVEL. Um RemoteOps com a UI thread pendurada continua segurando o mutex e continua
    /// recebendo o evento — mas a janela nunca sobe. Como a segunda instância encerrava em silêncio
    /// nesse caso, o operador clicava no ícone e não acontecia NADA, sem nenhuma explicação; a única
    /// saída que ele encontrava era reiniciar o Windows. Com o handshake, quem chama consegue
    /// distinguir "já está aberto, trouxe pra frente" de "tem um processo travado ali" e dizer isso ao
    /// usuário.</para>
    /// </summary>
    public bool SignalExistingInstanceAndWait(TimeSpan timeout)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(_ackName, out EventWaitHandle? ack))
            {
                // Sem canal de confirmação: instância antiga (pré-handshake) ou listener não registrado.
                return false;
            }

            using (ack)
            {
                ack.Reset(); // descarta confirmação velha de uma tentativa anterior
                if (!SignalExistingInstance())
                {
                    return false;
                }

                return ack.WaitOne(timeout);
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
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

        // Manual reset: quem sinaliza dá Reset antes de pedir, então o estado não "vaza" entre
        // tentativas, e uma confirmação não some se a outra ponta demorar um instante pra esperar.
        _ack = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, _ackName);

        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_signal.WaitOne(500) && !_disposed)
                    {
                        // A confirmação vem DEPOIS de onActivate retornar: é isso que a torna prova de
                        // vida. Se a UI thread estiver pendurada, o Dispatcher.Invoke aqui dentro não
                        // retorna, o ack nunca é setado, e a outra instância avisa o usuário.
                        onActivate();
                        _ack.Set();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return; // guard disposto: encerra a thread.
                }
                catch (Exception)
                {
                    // onActivate (Dispatcher.Invoke) pode lançar durante o shutdown do app
                    // (TaskCanceledException/InvalidOperationException com o dispatcher encerrando)
                    // quando uma 2ª instância sinaliza no meio de um fechamento. Ativar a janela é
                    // best-effort — nunca vale escalar pro handler de AppDomain e mostrar "Erro fatal"
                    // num fechamento limpo. Se _disposed já é true, o while sai no próximo teste.
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
        _ack?.Dispose();

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
