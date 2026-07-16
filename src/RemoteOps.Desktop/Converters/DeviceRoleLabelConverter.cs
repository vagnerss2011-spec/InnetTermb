using System;
using System.Globalization;
using System.Windows.Data;
using RemoteOps.Desktop.Domain;

namespace RemoteOps.Desktop.Converters;

/// <summary>
/// Converte a chave de papel normalizada (ex.: "router") no rótulo pt-BR ("Roteador") via
/// <see cref="DeviceCatalog.RoleLabel"/>. Usado no ItemTemplate do ComboBox "Tipo" — o
/// SelectedItem continua sendo a chave (que é o que persiste em <c>Asset.DeviceRole</c>).
/// </summary>
public sealed class DeviceRoleLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DeviceCatalog.RoleLabel(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
