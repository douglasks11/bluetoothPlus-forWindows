using System.Windows.Media;
using BluetoothPlus.Services;

namespace BluetoothPlus.Models;

public enum DeviceCategory
{
    Other, Headphones, Speaker, Gamepad, Phone, Computer, Keyboard, Mouse,
    Watch, TV, Car, Microphone, Printer, Camera, Health, Tag,
}

/// <summary>
/// Rótulo, ícone (glifo Segoe Fluent Icons) e cor de cada categoria. A cor tem
/// duas versões: tons claros para o tema escuro e tons fortes para o tema claro
/// (tons pastel somem sobre fundo branco).
/// </summary>
public sealed record CategoryStyle(string Label, string Glyph, Brush DarkThemeBrush, Brush LightThemeBrush)
{
    public Brush Brush => ThemeManager.IsLight ? LightThemeBrush : DarkThemeBrush;
}

public static class DeviceCategories
{
    private static readonly Dictionary<DeviceCategory, CategoryStyle> Styles = new()
    {
        //                                       rótulo                 glifo     tema escuro  tema claro
        [DeviceCategory.Headphones] = Make("Fone de ouvido", "", 0xA78BFA, 0x7C3AED),
        [DeviceCategory.Speaker] = Make("Caixa de som", "", 0xF472B6, 0xDB2777),
        [DeviceCategory.Gamepad] = Make("Controle", "", 0x34D399, 0x059669),
        [DeviceCategory.Phone] = Make("Celular", "", 0x60A5FA, 0x2563EB),
        [DeviceCategory.Computer] = Make("Computador", "", 0x38BDF8, 0x0369A1),
        [DeviceCategory.Keyboard] = Make("Teclado", "", 0xFBBF24, 0xB45309),
        [DeviceCategory.Mouse] = Make("Mouse", "", 0xFBBF24, 0xB45309),
        [DeviceCategory.Watch] = Make("Relógio / pulseira", "", 0xFB923C, 0xC2410C),
        [DeviceCategory.TV] = Make("TV / monitor", "", 0x22D3EE, 0x0E7490),
        [DeviceCategory.Car] = Make("Carro", "", 0xF87171, 0xDC2626),
        [DeviceCategory.Microphone] = Make("Microfone", "", 0xC084FC, 0x9333EA),
        [DeviceCategory.Printer] = Make("Impressora", "", 0x94A3B8, 0x475569),
        [DeviceCategory.Camera] = Make("Câmera", "", 0x94A3B8, 0x475569),
        [DeviceCategory.Health] = Make("Saúde / fitness", "", 0xFB7185, 0xE11D48),
        [DeviceCategory.Tag] = Make("Rastreador / beacon", "", 0xA3E635, 0x4D7C0F),
        [DeviceCategory.Other] = Make("Dispositivo", "", 0x9CA3AF, 0x4B5563),
    };

    public static CategoryStyle Style(DeviceCategory category) => Styles[category];

    private static CategoryStyle Make(string label, string glyph, uint darkThemeRgb, uint lightThemeRgb)
        => new(label, glyph, FrozenBrush(darkThemeRgb), FrozenBrush(lightThemeRgb));

    private static Brush FrozenBrush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
