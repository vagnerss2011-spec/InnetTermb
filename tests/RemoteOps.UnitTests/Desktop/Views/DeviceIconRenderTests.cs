using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Valida as geometrias vetoriais dos glifos de papel e o render do <see cref="DeviceIcon"/>. O
/// <c>DeviceIcon</c> engole <c>FormatException</c> de uma geometria ruim em produção (cai em
/// Geometry.Empty), então o primeiro teste chama <c>Geometry.Parse</c> DIRETO para pegar qualquer
/// path mini-language malformado. O segundo prova que o controle instancia e faz layout sem lançar
/// (sem logo → caminho de fallback do glifo).
/// </summary>
public sealed class DeviceIconRenderTests
{
    [Fact]
    public void EveryRoleGeometry_Parses()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            foreach (string? role in DeviceRoles.All.Append(null))
            {
                Geometry g = Geometry.Parse(DeviceCatalog.RoleGlyphGeometry(role));
                Assert.False(g.Bounds.IsEmpty, $"geometria vazia/degenerada p/ papel '{role}'");
            }
        });
        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void DeviceIcon_RendersEveryRole_Fallback_WithoutThrowing()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            foreach (string? role in DeviceRoles.All.Append(null))
            {
                // VendorKey conhecido mas SEM arquivo de logo no output de teste → caminho de fallback.
                var icon = new DeviceIcon { Role = role, VendorKey = "huawei" };
                var window = new Window
                {
                    Width = 60,
                    Height = 60,
                    Content = icon,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                }
                finally
                {
                    window.Close();
                }
            }
        });
        Assert.True(captured is null, captured?.ToString());
    }
}
