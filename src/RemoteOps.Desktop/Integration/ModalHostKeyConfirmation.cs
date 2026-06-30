using System.Windows;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

/// <summary>
/// Diálogo WPF TOFU genuinamente assíncrono via TaskCompletionSource.
/// NUNCA usa .GetAwaiter().GetResult() — evita deadlock no thread de conexão SSH (ADR-009 §FIX-1).
/// </summary>
internal sealed class ModalHostKeyConfirmation : IHostKeyConfirmation
{
    public Task<bool> ConfirmAsync(string host, string fingerprintHex, bool isChanged, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (tcs.Task.IsCompleted) return;

            string title = isChanged ? "⚠ AVISO: Host Key Alterada" : "Host Key Desconhecida";
            string body = isChanged
                ? $"A chave do servidor '{host}' FOI ALTERADA.\n\n" +
                  $"Fingerprint (SHA-256):\n{fingerprintHex}\n\n" +
                  "Isto pode indicar ataque man-in-the-middle.\nConectar mesmo assim?"
                : $"Servidor '{host}' apresentou uma nova chave.\n\n" +
                  $"Fingerprint (SHA-256):\n{fingerprintHex}\n\nConfiar nesta chave e conectar?";

            var result = MessageBox.Show(Application.Current.MainWindow, body, title,
                MessageBoxButton.YesNo,
                isChanged ? MessageBoxImage.Warning : MessageBoxImage.Question);

            tcs.TrySetResult(result == MessageBoxResult.Yes);
        });

        return tcs.Task;
    }
}
