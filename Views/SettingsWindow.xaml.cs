using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BluetoothPlus.Models;
using BluetoothPlus.Services;

namespace BluetoothPlus.Views;

public partial class SettingsWindow : Window
{
    /// <summary>Uma linha da tabela: tipo de aparelho + porcentagem do alerta (0 = desligado).</summary>
    public sealed class AlertRow : INotifyPropertyChanged
    {
        public AlertRow(DeviceCategory category, int value)
        {
            Category = category;
            _value = value;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DeviceCategory Category { get; }
        public string Label => DeviceCategories.Style(Category).Label;
        public string Glyph => DeviceCategories.Style(Category).Glyph;
        public Brush Brush => DeviceCategories.Style(Category).Brush;

        private int _value;
        public int Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueLabel)));
            }
        }

        public string ValueLabel => Value == 0 ? "Desligado" : $"{Value}%";
    }

    private sealed record AllChoice(int? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly List<AlertRow> _rows;
    private readonly Action _sendTestNotification;

    private SettingsWindow(Action sendTestNotification)
    {
        InitializeComponent();
        _sendTestNotification = sendTestNotification;

        _rows = Enum.GetValues<DeviceCategory>()
            .Select(c => new AlertRow(c, SettingsStore.GetBatteryAlert(c)))
            .OrderBy(r => r.Category == DeviceCategory.Other) // "Dispositivo" (genérico) por último
            .ThenBy(r => r.Label)
            .ToList();
        Rows.ItemsSource = _rows;

        AllBox.ItemsSource = new[] { new AllChoice(null, "Escolher…") }
            .Concat(Enumerable.Range(1, 10).Select(i => new AllChoice(i * 5, $"{i * 5}%")))
            .Append(new AllChoice(0, "Desligado"))
            .ToList();
        AllBox.SelectedIndex = 0;
    }

    public static bool Open(Window owner, Action sendTestNotification)
        => new SettingsWindow(sendTestNotification) { Owner = owner }.ShowDialog() == true;

    private void TestNotification_Click(object sender, RoutedEventArgs e) => _sendTestNotification();

    // ---------------------------------------------------------------- bateria

    private void AllBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AllBox.SelectedItem is not AllChoice { Value: int value }) return;
        foreach (var row in _rows) row.Value = value;
        AllBox.SelectedIndex = 0;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Value = SettingsStore.DefaultBatteryAlert;
    }

    // ---------------------------------------------------------------- salvar

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.SetBatteryAlerts(_rows.Select(r => KeyValuePair.Create(r.Category, r.Value)));
        DialogResult = true;
    }
}
