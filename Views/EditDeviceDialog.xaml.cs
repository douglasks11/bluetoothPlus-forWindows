using System.Windows;
using BluetoothPlus.Models;
using BluetoothPlus.Services;

namespace BluetoothPlus.Views;

public partial class EditDeviceDialog : Window
{
    /// <summary>Opção do seletor de tipo; Category null = identificação automática.</summary>
    public sealed record CategoryChoice(DeviceCategory? Category, string Label, string Glyph);

    private EditDeviceDialog(DeviceItem item)
    {
        InitializeComponent();

        Header.Text = item.DisplayName;
        SubHeader.Text = $"{item.Name} · {item.Address.ToUpperInvariant()}";
        NicknameBox.Text = item.Nickname ?? "";
        HiddenBox.IsChecked = item.IsHidden;

        // "Automático" mostra o que foi identificado, para o usuário saber o que está sobrescrevendo.
        var auto = DeviceCategories.Style(item.AutomaticCategory);
        var choices = new List<CategoryChoice> { new(null, $"Automático ({auto.Label})", auto.Glyph) };
        choices.AddRange(Enum.GetValues<DeviceCategory>()
            .Select(c => new CategoryChoice(c, DeviceCategories.Style(c).Label, DeviceCategories.Style(c).Glyph))
            .OrderBy(c => c.Label));
        CategoryBox.ItemsSource = choices;
        CategoryBox.SelectedItem = choices.First(c => c.Category == item.ManualCategory);

        Loaded += (_, _) => NicknameBox.Focus();
    }

    /// <summary>Abre o editor; se o usuário salvar, aplica e persiste as preferências.</summary>
    public static bool Edit(DeviceItem item, Window owner)
    {
        var dialog = new EditDeviceDialog(item) { Owner = owner };
        if (dialog.ShowDialog() != true) return false;

        var settings = new DeviceSettings
        {
            Nickname = string.IsNullOrWhiteSpace(dialog.NicknameBox.Text) ? null : dialog.NicknameBox.Text.Trim(),
            Category = (dialog.CategoryBox.SelectedItem as CategoryChoice)?.Category,
            Hidden = dialog.HiddenBox.IsChecked == true,
        };
        SettingsStore.Set(item.Address, settings);
        item.Apply(settings);
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
