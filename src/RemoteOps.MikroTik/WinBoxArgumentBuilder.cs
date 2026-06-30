using System.Net;
using System.Net.Sockets;

using RemoteOps.Contracts.ExternalTools;

namespace RemoteOps.MikroTik;

public static class WinBoxArgumentBuilder
{
    // Regras posicionais WinBox: <connect-to> [<login> [<password>]]
    // Workspace como 4º posicional não confirmado na CLI oficial — ver ADR-006.
    public static IReadOnlyList<string> Build(
        ExternalToolLaunchRequest request,
        string? password,
        bool passwordArgumentAllowed)
    {
        var args = new List<string>(capacity: 3);

        // Argumento 1: connect-to (sempre presente)
        args.Add(BuildConnectTo(request.Target));

        var hasLogin = !string.IsNullOrEmpty(request.Login);

        // Argumento 2: login (somente se não vazio)
        if (hasLogin)
            args.Add(request.Login!);

        // Argumento 3: senha (somente quando tudo permite E não vazia)
        // Login deve estar presente para que a senha fique na posição correta.
        var includePassword = hasLogin
            && passwordArgumentAllowed
            && request.IncludePasswordArgument
            && !string.IsNullOrEmpty(password);

        if (includePassword)
            args.Add(password!);

        return args.AsReadOnly();
    }

    public static string BuildConnectTo(ExternalToolTarget target)
    {
        var address = target.Address;
        var port = target.Port;

        if (IsIpv6Like(address))
        {
            var bare = StripBrackets(address);
            return port > 0 ? $"[{bare}]:{port}" : $"[{bare}]";
        }

        return port > 0 ? $"{address}:{port}" : address;
    }

    // internal: usado por WinBoxRunner para emitir winbox_ipv6_target_used.
    internal static bool IsIpv6Like(string address)
    {
        if (address.StartsWith('['))
            return true;

        return IPAddress.TryParse(address, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static string StripBrackets(string address)
    {
        if (!address.StartsWith('['))
            return address;

        var end = address.IndexOf(']');
        return end >= 0 ? address[1..end] : address[1..];
    }
}
