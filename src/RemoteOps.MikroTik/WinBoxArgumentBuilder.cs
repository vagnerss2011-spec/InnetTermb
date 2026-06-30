using System.Net;
using System.Net.Sockets;
using RemoteOps.MikroTik.Models;

namespace RemoteOps.MikroTik;

public static class WinBoxArgumentBuilder
{
    /// <summary>
    /// Builds the connect-to string: IPv6 literals get RFC 3986 brackets.
    /// </summary>
    public static string BuildConnectTo(WinBoxTarget target)
    {
        var address = target.Address;
        var port = target.Port;

        if (IsIPv6Literal(address))
            return $"[{address}]:{port}";

        return port == WinBoxTarget.DefaultPort ? address : $"{address}:{port}";
    }

    /// <summary>
    /// Populates the ArgumentList of a ProcessStartInfo.
    /// Password is NEVER included unless allowPassword is true.
    /// The ProcessStartInfo must not use shell execute.
    /// </summary>
    public static void PopulateArgumentList(
        IList<string> argumentList,
        WinBoxLaunchRequest request,
        bool allowPassword,
        string? password)
    {
        if (request.RoMon is { Enabled: true } romon)
        {
            argumentList.Add("--romon");
            argumentList.Add(romon.Agent ?? string.Empty);
            argumentList.Add(romon.ConnectTo ?? BuildConnectTo(request.Target));
        }
        else
        {
            argumentList.Add(BuildConnectTo(request.Target));
        }

        argumentList.Add(request.Login);

        if (allowPassword && !string.IsNullOrEmpty(password))
            argumentList.Add(password);
        else
            argumentList.Add(string.Empty);

        if (!string.IsNullOrWhiteSpace(request.WorkspaceName))
            argumentList.Add(request.WorkspaceName);
    }

    private static bool IsIPv6Literal(string address) =>
        IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetworkV6;
}
