using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace RemoteOps.Desktop.Converters;

/// <summary>
/// Formats an <see cref="RemoteOps.Contracts.Assets.Endpoint"/> list row as "protocol — address:port",
/// resolving the address from Ipv4/Ipv6/Fqdn (in that precedence order) since only one of the three is
/// ever populated for a given endpoint (see <c>HostEditorViewModel.AddEndpoint</c>). Expected binding
/// order: Protocol, Ipv4, Ipv6, Fqdn, Port.
/// </summary>
public sealed class EndpointAddressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 5)
            return string.Empty;

        var protocol = values[0] as string ?? string.Empty;
        var address = new[] { values[1] as string, values[2] as string, values[3] as string }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        var port = values[4];

        return $"{protocol} — {address}:{port}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
