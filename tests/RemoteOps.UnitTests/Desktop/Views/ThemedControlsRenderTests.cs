using System.Windows;
using System.Windows.Controls;
using RemoteOps.UnitTests.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Regressão de tema (v1.2.24): ContextMenu/MenuItem, ListBoxItem e Expander não tinham estilo
/// próprio e caíam no template CLARO padrão do WPF (Aero2). Os novos estilos implícitos
/// (Themes/Controls/Menus.xaml, ListBox.xaml, Expander.xaml) só são APLICADOS quando o controle é
/// realizado — o popup de um ContextMenu/submenu e o corpo de um Expander não existem na árvore
/// visual até abrir/expandir, então carregar o tema não basta para exercitá-los. Estes testes
/// abrem/expandem os controles de verdade (thread STA + tema real) para provar que os templates
/// aplicam e fazem layout sem lançar (ex.: binding quebrado, recurso ausente, parte obrigatória
/// faltando).
/// </summary>
public sealed class ThemedControlsRenderTests
{
    [Fact]
    public void ContextMenu_WithItems_Opens_WithoutThrowing()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var owner = new Button { Content = "host" };
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Conectar via SSH" });
            menu.Items.Add(new MenuItem { Header = "Editar" });
            menu.Items.Add(new Separator());
            // Item COM ícone (como o menu da conta) exercita a coluna de ícone do template.
            menu.Items.Add(new MenuItem
            {
                Header = "Configurações",
                Icon = new TextBlock { Text = "" },
            });
            // Cabeçalho de submenu exercita a seta + o Popup "PART_Popup" do template.
            var submenu = new MenuItem { Header = "Mais" };
            submenu.Items.Add(new MenuItem { Header = "Abrir WinBox" });
            menu.Items.Add(submenu);
            owner.ContextMenu = menu;

            var window = new Window
            {
                Width = 240,
                Height = 200,
                Content = owner,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                menu.PlacementTarget = owner;
                menu.IsOpen = true; // realiza o popup + aplica o template de cada MenuItem
                window.UpdateLayout();
                menu.UpdateLayout();
            }
            finally
            {
                menu.IsOpen = false;
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void ListBox_WithSelectedItem_WithoutThrowing()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var list = new ListBox();
            list.Items.Add("ssh 10.0.0.1:22");
            list.Items.Add("telnet 10.0.0.2:23");

            var window = new Window
            {
                Width = 240,
                Height = 200,
                Content = list,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                list.SelectedIndex = 0; // exercita o gatilho IsSelected (fundo de acento + borda)
                window.UpdateLayout();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Fact]
    public void Expander_Expanded_WithoutThrowing()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var expander = new Expander
            {
                Header = "Ver o que será anexado",
                IsExpanded = true, // realiza o ExpandSite + o header ToggleButton retemplado
                Content = new TextBlock { Text = "versão, sistema, últimas linhas de log" },
            };

            var window = new Window
            {
                Width = 320,
                Height = 200,
                Content = expander,
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
        });

        Assert.True(captured is null, captured?.ToString());
    }
}
