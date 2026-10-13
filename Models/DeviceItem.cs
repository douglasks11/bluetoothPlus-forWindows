using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Windows.Devices.Enumeration;

namespace BluetoothPlus.Models;

public enum DeviceKind { Classic, LowEnergy }

public sealed class DeviceItem : INotifyPropertyChanged
{
    public const int HistorySize = 120;
    public const string UnnamedLabel = "(sem nome)";

    public DeviceItem(string address)
    {
        Address = address;
        Angle = StableAngle(address);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Endereço MAC normalizado (aa:bb:cc:dd:ee:ff) — chave única.</summary>
    public string Address { get; }

    /// <summary>Entrada do Windows (AssociationEndpoint) usada para parear/desparear.</summary>
    public DeviceInformation? Info { get; set; }

    /// <summary>Agrupa os nós PnP do mesmo aparelho; usado para ler a bateria de dispositivos Clássicos.</summary>
    public Guid? ContainerId { get; set; }

    /// <summary>Ângulo fixo no radar, derivado do endereço para não "pular" entre execuções.</summary>
    public double Angle { get; }

    public Queue<int> RssiHistory { get; } = new();

    private string _name = UnnamedLabel;
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            _nameCategory = DeviceClassifier.FromName(value);
            OnChanged(nameof(DisplayName), nameof(IsUnnamed));
            OnCategoryChanged();
        }
    }

    // ------------------------------------------------------------ preferências do usuário

    private string? _nickname;
    /// <summary>Apelido dado pelo usuário; substitui o nome do aparelho na tela.</summary>
    public string? Nickname
    {
        get => _nickname;
        set
        {
            var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Set(ref _nickname, clean)) OnChanged(nameof(DisplayName), nameof(IsUnnamed));
        }
    }

    private DeviceCategory? _manualCategory;
    /// <summary>Tipo escolhido pelo usuário; vence a identificação automática.</summary>
    public DeviceCategory? ManualCategory
    {
        get => _manualCategory;
        set { if (Set(ref _manualCategory, value)) OnCategoryChanged(); }
    }

    private bool _isHidden;
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    public string DisplayName => Nickname ?? Name;

    public void Apply(Services.DeviceSettings? settings)
    {
        Nickname = settings?.Nickname;
        ManualCategory = settings?.Category;
        IsHidden = settings?.Hidden ?? false;
    }
    public bool IsUnnamed => Nickname is null && Name == UnnamedLabel;

    private DeviceKind _kind;
    public DeviceKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) OnChanged(nameof(KindLabel), nameof(Subtitle)); }
    }

    private int? _rssi;
    public int? Rssi
    {
        get => _rssi;
        private set { if (Set(ref _rssi, value)) OnChanged(nameof(RssiLabel), nameof(SignalLevel)); }
    }

    private bool _isPaired;
    public bool IsPaired
    {
        get => _isPaired;
        set { if (Set(ref _isPaired, value)) OnChanged(nameof(StatusLabel), nameof(GroupLabel)); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { if (Set(ref _isConnected, value)) OnChanged(nameof(StatusLabel)); }
    }

    private int? _battery;
    public int? Battery
    {
        get => _battery;
        set { if (Set(ref _battery, value)) OnChanged(nameof(BatteryLabel), nameof(BatteryGlyph), nameof(HasBattery)); }
    }

    // ------------------------------------------------------------ categoria

    // Declarada pelo aparelho (Class of Device / Appearance) vence o palpite pelo nome.
    private DeviceCategory _reportedCategory;
    private DeviceCategory _nameCategory;

    public DeviceCategory Category => ManualCategory ?? AutomaticCategory;

    /// <summary>Tipo identificado sem a escolha manual do usuário.</summary>
    public DeviceCategory AutomaticCategory => _reportedCategory != DeviceCategory.Other ? _reportedCategory : _nameCategory;

    public void SetReportedCategory(DeviceCategory category)
    {
        if (category == DeviceCategory.Other || category == _reportedCategory) return;
        _reportedCategory = category;
        OnCategoryChanged();
    }

    private CategoryStyle Style => DeviceCategories.Style(Category);
    public string CategoryLabel => Style.Label;
    public string CategoryGlyph => Style.Glyph;
    public Brush CategoryBrush => Style.Brush;

    // ------------------------------------------------------------ exibição

    public DateTime LastSeen { get; private set; } = DateTime.Now;

    /// <summary>Total de leituras de sinal recebidas — permite detectar uma leitura nova.</summary>
    public long SampleCount { get; private set; }

    public string KindLabel => Kind == DeviceKind.LowEnergy ? "BLE" : "Clássico";
    public string Subtitle => $"{CategoryLabel} · {KindLabel}";
    public string RssiLabel => Rssi is int r ? $"{r} dBm" : "—";
    public string BatteryLabel => Battery is int b ? $"{b}%" : "—";
    public bool HasBattery => Battery is not null;
    public string StatusLabel => IsConnected ? "Conectado" : IsPaired ? "Pareado" : "Disponível";
    public const string PairedGroup = "Meus dispositivos";
    public const string NearbyGroup = "Por perto";
    public string GroupLabel => IsPaired ? PairedGroup : NearbyGroup;

    /// <summary>0–4 barras, nos mesmos limiares usados por indicadores de Wi-Fi/Bluetooth.</summary>
    public int SignalLevel => Rssi switch
    {
        null => 0,
        >= -55 => 4,
        >= -67 => 3,
        >= -78 => 2,
        >= -90 => 1,
        _ => 0,
    };

    /// <summary>Glifo de bateria do Segoe Fluent Icons (Battery0 … Battery10).</summary>
    public string BatteryGlyph => Battery switch
    {
        null => "",
        >= 95 => "\uE83F",
        int b => ((char)(0xE850 + b / 10)).ToString(),
    };

    public void Touch() => LastSeen = DateTime.Now;

    public void AddRssi(int rssi)
    {
        // O Windows reporta -127 / 0 quando não há leitura válida.
        if (rssi <= -127 || rssi >= 0) return;
        Rssi = rssi;
        RssiHistory.Enqueue(rssi);
        while (RssiHistory.Count > HistorySize) RssiHistory.Dequeue();
        SampleCount++;
        Touch();
    }

    /// <summary>A cor da categoria depende do tema; avisa a tela quando ele muda.</summary>
    public void NotifyThemeChanged() => OnChanged(nameof(CategoryBrush));

    private void OnCategoryChanged() => OnChanged(nameof(Category), nameof(CategoryLabel), nameof(CategoryGlyph),
        nameof(CategoryBrush), nameof(Subtitle));

    private static double StableAngle(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) h = (h ^ c) * 16777619;
        return h % 360 * Math.PI / 180.0;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(params string?[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
