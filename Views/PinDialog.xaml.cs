using System.Windows;
using System.Windows.Input;

namespace BluetoothPlus.Views;

public partial class PinDialog : Window
{
    private PinDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    /// <summary>Pede o PIN ao usuário; retorna null se cancelado.</summary>
    public static string? Ask(Window? owner)
    {
        var dialog = new PinDialog();
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.PinBox.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
