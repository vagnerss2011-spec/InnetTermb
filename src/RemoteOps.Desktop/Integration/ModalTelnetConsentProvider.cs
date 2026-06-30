using System.Windows;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

/// <summary>
/// Bloqueia a abertura da conexão TCP até ack explícito do usuário (ADR-009 §FIX-2).
/// Telnet transmite tudo em texto puro — o aviso é obrigatório e não pode ser ignorado silenciosamente.
/// </summary>
internal sealed class ModalTelnetConsentProvider : ITelnetConsentProvider
{
    public Task<bool> RequestConsentAsync(string host, int port, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (tcs.Task.IsCompleted) return;

            var result = MessageBox.Show(Application.Current.MainWindow,
                $"Você está prestes a abrir uma sessão Telnet para '{host}:{port}'.\n\n" +
                "⚠ Telnet transmite todos os dados em TEXTO PURO, incluindo senhas.\n\n" +
                "Esta conexão é autorizada e o risco é aceitável?",
                "Aviso de Segurança — Telnet Sem Criptografia",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            tcs.TrySetResult(result == MessageBoxResult.Yes);
        });

        return tcs.Task;
    }
}
