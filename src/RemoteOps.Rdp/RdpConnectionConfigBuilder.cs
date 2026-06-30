using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>
/// Monta a configuração de conexão RDP a partir de Endpoint + usuário resolvido.
/// Classe pura — sem COM, sem UI — testável em qualquer plataforma.
/// </summary>
public static class RdpConnectionConfigBuilder
{
    private const int DefaultPort = 3389;

    public static RdpConnectionConfig Build(
        Endpoint endpoint,
        string username,
        bool preferIpv6,
        RdpRedirectionPolicy? redirectionPolicy = null)
    {
        string host = ResolveHost(endpoint, preferIpv6);
        int port = endpoint.Port > 0 ? endpoint.Port : DefaultPort;

        return new RdpConnectionConfig
        {
            Host = host,
            Port = port,
            Username = username,
            // NLA é obrigatório no MVP — sem opção de desligar (requisito de segurança).
            NlaRequired = true,
            Redirection = redirectionPolicy ?? RdpRedirectionPolicy.Default,
        };
    }

    public static string ResolveHost(Endpoint endpoint, bool preferIpv6)
    {
        if (preferIpv6 && !string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv4)) return endpoint.Ipv4;
        if (!string.IsNullOrWhiteSpace(endpoint.Fqdn)) return endpoint.Fqdn;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        throw new InvalidOperationException($"Endpoint '{endpoint.Id}' não tem endereço resolvível.");
    }
}
