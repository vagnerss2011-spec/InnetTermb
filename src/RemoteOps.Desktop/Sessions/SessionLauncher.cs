using System;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.Terminal;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;
using RemoteOps.Rdp;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Sessions;

/// <summary>
/// Fonte única de "abrir um host por protocolo". Substitui a lógica antes espalhada
/// em MainViewModel.OnSessionRequested e InspectorViewModel.OpenWinBoxAsync.
/// </summary>
public sealed class SessionLauncher
{
    private static readonly string[] Preference = { RemoteProtocol.Ssh, RemoteProtocol.Telnet, RemoteProtocol.Rdp, RemoteProtocol.MikroTik };

    private readonly TabsViewModel _tabs;
    private readonly IWinBoxRunner? _winBox;
    private readonly IFeatureFlags? _flags;
    private readonly ITerminalSessionProvider? _ssh;
    private readonly ITerminalSessionProvider? _telnet;
    private readonly IRdpSessionProvider? _rdp;
    private readonly IRdpCredentialResolver? _rdpCred;
    private readonly ICredentialRefResolver? _credentials;
    private readonly IExternalTerminalLauncher? _externalTerminal;

    public SessionLauncher(
        TabsViewModel tabs,
        IWinBoxRunner? winBox,
        IFeatureFlags? flags,
        ITerminalSessionProvider? ssh,
        ITerminalSessionProvider? telnet,
        IRdpSessionProvider? rdp,
        IRdpCredentialResolver? rdpCred,
        ICredentialRefResolver? credentials = null,
        IExternalTerminalLauncher? externalTerminal = null)
    {
        _tabs = tabs;
        _winBox = winBox;
        _flags = flags;
        _ssh = ssh;
        _telnet = telnet;
        _rdp = rdp;
        _rdpCred = rdpCred;
        _credentials = credentials;
        _externalTerminal = externalTerminal;
    }

    public string PrimaryProtocol(Asset asset)
    {
        foreach (var p in Preference)
        {
            if (asset.Endpoints.Any(e => e.Protocol == p))
            {
                return p;
            }
        }
        return asset.Endpoints.Count > 0 ? asset.Endpoints[0].Protocol : RemoteProtocol.Ssh;
    }

    public bool CanLaunch(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return false;
        }
        return protocol switch
        {
            RemoteProtocol.Ssh => _externalTerminal != null || _ssh != null,
            RemoteProtocol.Telnet => _telnet != null,
            RemoteProtocol.Rdp => (_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false) && _rdp != null && _rdpCred != null,
            RemoteProtocol.MikroTik => _winBox != null,
            _ => false,
        };
    }

    /// <summary>
    /// Lança a sessão do protocolo pedido. NUNCA falha em silêncio: todo caminho de
    /// erro devolve <see cref="LaunchResult.Fail"/> com orientação acionável em pt-BR
    /// (antes era return vazio/aba morta — o operador clicava e "nada acontecia").
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return LaunchResult.Fail(
                $"O host \"{asset.Name}\" não tem endpoint {protocol.ToUpperInvariant()}. Edite o host e adicione um endpoint desse protocolo.");
        }

        if (protocol == RemoteProtocol.MikroTik)
        {
            return await LaunchWinBoxAsync(asset, ep);
        }

        if (protocol == RemoteProtocol.Rdp)
        {
            if (!(_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false))
            {
                return LaunchResult.Fail(
                    "RDP está desabilitado. Habilite em Configurações → Recursos (rdp.enabled) e reinicie o app.");
            }
            if (_rdp is null || _rdpCred is null)
            {
                return LaunchResult.Fail("O provedor RDP não está disponível nesta instalação.");
            }
            if (ep.CredentialRefId is null)
            {
                return LaunchResult.Fail(
                    "O endpoint RDP não tem credencial. Crie uma no Keychain e selecione-a no editor do host.");
            }
            var rdpReq = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
            _tabs.OpenRdpTab(new RdpTabViewModel(rdpReq.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, _rdp, _rdpCred, rdpReq));
            return LaunchResult.Ok();
        }

        // SSH abre num terminal REAL do Windows (por fora do app), quando o launcher externo
        // está disponível. Substitui o terminal WebView2, que em algumas GPUs renderizava
        // escuro/travado. O terminal nativo integrado (sem WebView2) vem numa etapa seguinte.
        if (protocol == RemoteProtocol.Ssh && _externalTerminal != null)
        {
            return await LaunchExternalSshAsync(asset, ep);
        }

        var provider = protocol == RemoteProtocol.Ssh ? _ssh : protocol == RemoteProtocol.Telnet ? _telnet : null;
        if (provider is null)
        {
            return LaunchResult.Fail(
                $"O provedor {protocol.ToUpperInvariant()} está indisponível nesta instalação.");
        }
        if (ep.CredentialRefId is null)
        {
            return LaunchResult.Fail(
                $"O endpoint {protocol.ToUpperInvariant()} não tem credencial. Crie uma no Keychain (login e senha) e selecione-a no editor do host.");
        }

        var req = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
        _tabs.OpenTerminalTab(new TerminalTabViewModel(req.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, provider, req));
        return LaunchResult.Ok();
    }

    private async Task<LaunchResult> LaunchWinBoxAsync(Asset asset, Endpoint ep)
    {
        if (_winBox is null)
        {
            return LaunchResult.Fail("O WinBox não está disponível nesta instalação.");
        }
        var (address, family) = ResolveAddress(ep);
        var request = new ExternalToolLaunchRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = asset.WorkspaceId,
            Tool = "winbox",
            HostId = asset.Id,
            Target = new ExternalToolTarget { Address = address, AddressFamily = family, Port = ep.Port, PreferIpv6 = ep.PreferIpv6 },
            CredentialRefId = ep.CredentialRefId,
            IncludePasswordArgument = false,
            RequestedBy = "local-user",
            RequestedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await _winBox.LaunchAsync(request);
            return LaunchResult.Ok();
        }
        catch (WinBoxValidationException ex)
        {
            // Fail-closed do manifesto (ex.: sha256 ausente/divergente) — antes morria
            // numa Task descartada e o clique "não fazia nada".
            return LaunchResult.Fail(
                $"WinBox bloqueado pela validação: {ex.Message} Configure o executável em Configurações → Ferramentas externas.");
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail($"Falha ao abrir o WinBox: {ex.Message}");
        }
    }

    private async Task<LaunchResult> LaunchExternalSshAsync(Asset asset, Endpoint ep)
    {
        if (ep.CredentialRefId is null)
        {
            return LaunchResult.Fail(
                "O endpoint SSH não tem credencial. Crie uma no Keychain (login e senha) e selecione-a no editor do host.");
        }

        var (host, _) = ResolveAddress(ep);
        if (string.IsNullOrWhiteSpace(host))
        {
            return LaunchResult.Fail(
                $"O host \"{asset.Name}\" não tem endereço válido. Edite o host e informe um IP ou domínio.");
        }

        // O usuário vem dos metadados da credencial (não é segredo — não abre o cofre).
        string? username = null;
        if (_credentials != null)
        {
            try
            {
                var cred = await _credentials.ResolveAsync(ep.CredentialRefId).ConfigureAwait(false);
                username = cred.Metadata?.Username;
            }
            catch
            {
                // Sem usuário resolvido: o ssh usa o padrão / pergunta. Não é motivo pra falhar.
            }
        }

        int port = ep.Port > 0 ? ep.Port : 22;
        try
        {
            await _externalTerminal!.LaunchSshAsync(new SshLaunchTarget(host, port, username)).ConfigureAwait(false);
            return LaunchResult.Ok();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ssh.exe ausente (OpenSSH Client não instalado).
            return LaunchResult.Fail(
                "O cliente OpenSSH (ssh.exe) não foi encontrado no Windows. Ative em Configurações → Sistema → " +
                "Componentes opcionais → adicionar \"Cliente OpenSSH\" e tente de novo.");
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail($"Falha ao abrir o terminal SSH externo: {ex.Message}");
        }
    }

    private static (string address, string? family) ResolveAddress(Endpoint ep)
    {
        if (ep.PreferIpv6 && ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        if (ep.Ipv4 != null) return (ep.Ipv4, "ipv4");
        if (ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        return (ep.Fqdn ?? string.Empty, ep.Fqdn != null ? "dns" : null);
    }
}
