using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace RemoteOps.Cloud.Configuration;

/// <summary>
/// Configura em quais PROXIES a API confia para ler o <c>X-Forwarded-For</c>.
///
/// PORQUÊ: atrás do Caddy (docker-compose), toda request chega com o IP do container do
/// proxy. Caddy e api são serviços separados na bridge do Compose, então a api vê o IP da
/// bridge (172.x), que não é loopback. Com o <see cref="ForwardedHeadersOptions"/> padrão
/// (confia só em loopback), o <c>X-Forwarded-For</c> do Caddy é IGNORADO e
/// <c>ctx.Connection.RemoteIpAddress</c> fica preso no IP fixo do Caddy. Consequências:
///  - o rate limit do /auth (particionado por IP) vira um balde GLOBAL de 20/min — um
///    único cliente derruba a autenticação de todos (DoS);
///  - os logs de IP do /auth gravam sempre o Caddy (auditoria inútil).
///
/// Por isso confiamos EXPLICITAMENTE nas faixas privadas das bridges do Docker e limpamos
/// o default de loopback. As faixas são configuráveis por <c>TRUSTED_PROXY_CIDR</c> (lista
/// CIDR separada por vírgula) porque a sub-rede exata do Compose varia por host. Ver
/// docs/runbook-deploy-debian.md e adr/ADR-009.
///
/// ANTI-SPOOF: de uma origem FORA das faixas confiáveis o <c>X-Forwarded-For</c> é ignorado
/// — um cliente na internet não consegue forjar a própria origem. <c>ForwardLimit = 1</c>
/// confia só no hop imediato (o Caddy), então nem uma cadeia de XFF fabricada empurra o IP
/// real para fora da janela.
/// </summary>
public static class ForwardedHeadersSetup
{
    /// <summary>
    /// Default: faixas privadas dos pools de IPAM padrão do Docker. <c>172.16.0.0/12</c>
    /// cobre as user-defined bridges do Compose; <c>10.0.0.0/8</c> cobre redes
    /// overlay/customizadas. Sobrescreva com <c>TRUSTED_PROXY_CIDR</c> se o seu deploy usar
    /// outra sub-rede.
    /// </summary>
    public const string DefaultTrustedProxyCidrs = "172.16.0.0/12,10.0.0.0/8";

    /// <summary>
    /// Registra o <see cref="ForwardedHeadersOptions"/> confiando SÓ nas faixas resolvidas.
    /// Precisa ser combinado com <c>app.UseForwardedHeaders()</c> ANTES do rate-limiter no
    /// pipeline (ver Program.cs).
    /// </summary>
    public static void Configure(IServiceCollection services, IConfiguration config)
    {
        var networks = ParseTrustedNetworks(config["TRUSTED_PROXY_CIDR"]);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // Zera o default de loopback: atrás do Compose o proxy nunca é loopback.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
        });
    }

    /// <summary>
    /// Faz o parse do <c>TRUSTED_PROXY_CIDR</c> (lista CIDR separada por vírgula). Vazio/nulo
    /// cai em <see cref="DefaultTrustedProxyCidrs"/>. Lança <see cref="InvalidOperationException"/>
    /// se algum CIDR for inválido — melhor não subir do que subir confiando na rede errada
    /// (falha rápido, no mesmo espírito do DeploymentConfig).
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseTrustedNetworks(string? cidrCsv)
    {
        var source = string.IsNullOrWhiteSpace(cidrCsv) ? DefaultTrustedProxyCidrs : cidrCsv;

        var networks = new List<IPNetwork>();
        foreach (var raw in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                // System.Net.IPNetwork.Parse valida e normaliza (mascara os bits de host).
                networks.Add(IPNetwork.Parse(raw));
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"TRUSTED_PROXY_CIDR contém um CIDR inválido: '{raw}'. Use notação CIDR, ex.: 172.16.0.0/12.");
            }
        }

        if (networks.Count == 0)
            throw new InvalidOperationException(
                "TRUSTED_PROXY_CIDR não pode conter apenas separadores. Deixe a variável vazia para " +
                "usar o default das bridges do Docker.");

        return networks;
    }
}
