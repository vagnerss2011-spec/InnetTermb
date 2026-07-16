using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteOps.Desktop.Domain;
// UseWindowsForms=true injeta System.Drawing como global using → Brush/Brushes ficam ambíguos.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Ícone híbrido do device: mostra o LOGO do vendor (assets/logos/&lt;vendorKey&gt;.png) quando o
/// arquivo existe, senão cai no GLIFO de papel (geometria vetorial do <see cref="DeviceCatalog"/>)
/// tingido pela cor do vendor. Reutilizado na lista e no editor. O fallback garante que nunca fica
/// sem ícone — mesmo sem nenhum logo instalado (assets/logos/ é gitignored).
/// </summary>
public partial class DeviceIcon : UserControl
{
    public DeviceIcon()
    {
        InitializeComponent();
        UpdateVisual();
    }

    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role), typeof(string), typeof(DeviceIcon),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty VendorKeyProperty = DependencyProperty.Register(
        nameof(VendorKey), typeof(string), typeof(DeviceIcon),
        new PropertyMetadata(null, OnVisualChanged));

    /// <summary>Papel normalizado (ver <c>DeviceRoles</c>) — escolhe o glifo de fallback.</summary>
    public string? Role
    {
        get => (string?)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    /// <summary>Chave do vendor — escolhe o logo e a cor do glifo.</summary>
    public string? VendorKey
    {
        get => (string?)GetValue(VendorKeyProperty);
        set => SetValue(VendorKeyProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DeviceIcon)d).UpdateVisual();

    private void UpdateVisual()
    {
        if (TryLoadLogo())
        {
            LogoImage.Visibility = Visibility.Visible;
            GlyphPath.Visibility = Visibility.Collapsed;
            return;
        }

        // Fallback: glifo de papel tingido pela cor do vendor.
        try
        {
            GlyphPath.Data = Geometry.Parse(DeviceCatalog.RoleGlyphGeometry(Role));
        }
        catch (FormatException)
        {
            GlyphPath.Data = Geometry.Empty;
        }
        GlyphPath.Stroke = VendorBrush();
        GlyphPath.Visibility = Visibility.Visible;
        LogoImage.Visibility = Visibility.Collapsed;
    }

    private bool TryLoadLogo()
    {
        string? file = DeviceCatalog.LogoFileName(VendorKey);
        if (file is null)
        {
            return false;
        }

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logos", file);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // não trava o arquivo
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            LogoImage.Source = bmp;
            return true;
        }
        catch (Exception)
        {
            return false; // arquivo corrompido/ilegível → cai no glifo
        }
    }

    private Brush VendorBrush()
    {
        try
        {
            object? converted = new BrushConverter().ConvertFromString(DeviceCatalog.VendorColorHex(VendorKey));
            if (converted is Brush brush)
            {
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // hex inválido — cai no padrão abaixo
        }
        return Brushes.Gray;
    }
}
