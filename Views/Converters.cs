using System.Globalization;
using System.Windows.Data;

namespace BluetoothPlus.Views;

/// <summary>SignalLevel (0–4) → opacidade da barra cujo índice (1–4) vem no ConverterParameter.</summary>
public sealed class SignalBarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int l ? l : 0;
        var bar = int.Parse((string)parameter, CultureInfo.InvariantCulture);
        return level >= bar ? 1.0 : 0.18;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
