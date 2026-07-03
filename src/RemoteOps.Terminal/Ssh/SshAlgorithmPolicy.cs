using System.Collections.Generic;
using Renci.SshNet;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Aplica o perfil de segurança SSH ao <see cref="ConnectionInfo"/>, no único ponto onde a
/// conexão é montada. SSH.NET 2024.2.0 já habilita TODOS os algoritmos (fortes e fracos) por
/// default — então "auto" conecta a equipamento legado sem ação. "strict" REMOVE os fracos
/// (só desabilita — nenhuma criptografia nova). Perfil por host: o hardening não afeta a frota.
/// </summary>
public static class SshAlgorithmPolicy
{
    public const string Auto = "auto";
    public const string Strict = "strict";

    private static readonly string[] WeakKex =
    {
        "diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1",
    };
    private static readonly string[] WeakHostKey =
    {
        "ssh-rsa", "ssh-dss", "ssh-rsa-cert-v01@openssh.com", "ssh-dss-cert-v01@openssh.com",
    };
    private static readonly string[] WeakCiphers =
    {
        "aes128-cbc", "aes192-cbc", "aes256-cbc", "3des-cbc",
    };
    private static readonly string[] WeakHmac =
    {
        "hmac-sha1", "hmac-sha1-etm@openssh.com",
    };

    public static void Apply(ConnectionInfo info, string? profile)
    {
        if (profile != Strict)
        {
            return; // auto/null: defaults permissivos da lib (conecta a legado)
        }

        Remove(info.KeyExchangeAlgorithms, WeakKex);
        Remove(info.HostKeyAlgorithms, WeakHostKey);
        Remove(info.Encryptions, WeakCiphers);
        Remove(info.HmacAlgorithms, WeakHmac);
    }

    private static void Remove<T>(IDictionary<string, T> algos, string[] names)
    {
        foreach (string name in names)
        {
            algos.Remove(name); // no-op se ausente; nunca lança
        }
    }
}
