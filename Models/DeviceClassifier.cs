using System.Text.RegularExpressions;

namespace BluetoothPlus.Models;

/// <summary>
/// Descobre o tipo do aparelho. Ordem de confiança:
/// 1. Class of Device (Bluetooth Clássico) — o próprio aparelho declara;
/// 2. GAP Appearance (BLE) — idem;
/// 3. Nome — heurística para quem não declara nada.
/// </summary>
public static class DeviceClassifier
{
    /// <summary>Class of Device: major (5 bits) e minor (6 bits) do Bluetooth Assigned Numbers.</summary>
    public static DeviceCategory FromClassOfDevice(int major, int minor) => major switch
    {
        1 => DeviceCategory.Computer,
        2 => DeviceCategory.Phone,
        4 => minor switch
        {
            1 or 2 or 6 => DeviceCategory.Headphones,   // headset, hands-free, headphones
            4 => DeviceCategory.Microphone,
            5 or 7 or 10 => DeviceCategory.Speaker,       // loudspeaker, áudio portátil, HiFi
            8 => DeviceCategory.Car,
            12 or 13 => DeviceCategory.Camera,
            14 or 15 or 9 => DeviceCategory.TV,           // monitor, display+som, set-top box
            18 => DeviceCategory.Gamepad,
            _ => DeviceCategory.Speaker,
        },
        5 => (minor & 0x0F) switch
        {
            1 or 2 => DeviceCategory.Gamepad,             // joystick, gamepad
            _ => (minor >> 4) switch
            {
                1 or 3 => DeviceCategory.Keyboard,        // teclado, combo
                2 => DeviceCategory.Mouse,
                _ => DeviceCategory.Other,
            },
        },
        6 => (minor & 0x20) != 0 ? DeviceCategory.Printer
           : (minor & 0x08) != 0 ? DeviceCategory.Camera
           : (minor & 0x04) != 0 ? DeviceCategory.TV
           : DeviceCategory.Printer,
        7 => minor == 1 ? DeviceCategory.Watch : DeviceCategory.Other,
        8 => DeviceCategory.Gamepad,
        9 => DeviceCategory.Health,
        _ => DeviceCategory.Other,
    };

    /// <summary>GAP Appearance (16 bits): categoria = bits 15..6, subcategoria = bits 5..0.</summary>
    public static DeviceCategory FromAppearance(int appearance)
    {
        int category = appearance >> 6, sub = appearance & 0x3F;
        return category switch
        {
            0x01 => DeviceCategory.Phone,
            0x02 => DeviceCategory.Computer,
            0x03 => DeviceCategory.Watch,
            0x05 => DeviceCategory.TV,
            0x06 => DeviceCategory.Gamepad,               // controle remoto
            0x08 or 0x09 => DeviceCategory.Tag,           // tag, chaveiro
            0x0A => DeviceCategory.Speaker,               // media player
            0x0C or 0x0D or 0x0E or 0x10 or 0x11 or 0x12 or 0x31 => DeviceCategory.Health,
            0x0F => sub switch                            // HID
            {
                0x01 => DeviceCategory.Keyboard,
                0x02 => DeviceCategory.Mouse,
                0x03 or 0x04 => DeviceCategory.Gamepad,
                _ => DeviceCategory.Other,
            },
            0x21 => DeviceCategory.Speaker,               // audio sink
            0x22 => DeviceCategory.Microphone,            // audio source
            0x25 => DeviceCategory.Headphones,            // wearable audio (earbud, headset)
            _ => DeviceCategory.Other,
        };
    }

    private static readonly (Regex Pattern, DeviceCategory Category)[] NameRules =
    {
        (Rx(@"buds|airpods|pods\b|headphone|headset|earbud|\bfone|\bwh-|\bwf-|freebuds|jabra|beats|\btws\b|\bjbl (tune|live|wave|endurance|vibe|reflect)|soundcore (life|liberty|space)"), DeviceCategory.Headphones),
        (Rx(@"controller|gamepad|xbox|dualsense|dualshock|joy-?con|8bitdo|\bcontrole"), DeviceCategory.Gamepad),
        (Rx(@"speaker|caixa|soundbar|soundlink|\bjbl|ue ?boom|megaboom|wonderboom|sonos|\becho\b|homepod|partybox|soundcore|marshall"), DeviceCategory.Speaker),
        (Rx(@"keyboard|teclado|\bkeys\b|\bk\d{3}\b"), DeviceCategory.Keyboard),
        (Rx(@"mouse|mx (master|anywhere)|\bm\d{3}\b"), DeviceCategory.Mouse),
        (Rx(@"watch|\bband\b|mi band|rel[oó]gio|fitbit|amazfit|garmin"), DeviceCategory.Watch),
        (Rx(@"iphone|galaxy|pixel|phone|celular|motorola|\bmoto\b|redmi|xiaomi|\bpoco\b|realme"), DeviceCategory.Phone),
        (Rx(@"laptop|notebook|desktop|macbook|\bpc\b|-pc\b"), DeviceCategory.Computer),
        (Rx(@"\btv\b|bravia|roku|fire ?stick|chromecast|monitor"), DeviceCategory.TV),
        (Rx(@"\bcar\b|carro|multim[ií]dia|uconnect|\bsync\b|mylink"), DeviceCategory.Car),
        (Rx(@"\bmic\b|microfone|microphone"), DeviceCategory.Microphone),
        (Rx(@"printer|impressora"), DeviceCategory.Printer),
        (Rx(@"airtag|smarttag|\btile\b|beacon"), DeviceCategory.Tag),
    };

    public static DeviceCategory FromName(string name)
    {
        foreach (var (pattern, category) in NameRules)
            if (pattern.IsMatch(name)) return category;
        return DeviceCategory.Other;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
