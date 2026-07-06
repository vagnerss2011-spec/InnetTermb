using System;
using Renci.SshNet.Common;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Traduz as exceções da SSH.NET para mensagens acionáveis em pt-BR. Antes, uma senha
/// errada ou um timeout subiam como texto técnico em inglês enterrado na aba — o operador
/// não sabia se o problema era a credencial, a rede ou o algoritmo.
/// </summary>
internal static class SshConnectionError
{
    public static string Describe(Exception ex, string host, int port)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SshAuthenticationException:
                    return $"Autenticação falhou: usuário ou senha incorretos para {host}.";
                case SshOperationTimeoutException:
                    return $"Tempo esgotado ao conectar em {host}:{port} — verifique rede, firewall ou se o host está acessível.";
                case SshConnectionException:
                    return $"Falha na conexão SSH com {host}:{port}. O equipamento pode exigir algoritmos legados; " +
                           "confirme que a Segurança SSH do host está em \"Automático\".";
            }
        }

        return $"Falha ao conectar em {host}:{port}: {ex.Message}";
    }
}
