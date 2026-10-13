using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BluetoothPlus.Models;

namespace BluetoothPlus.Services;

/// <summary>Preferências do usuário para um aparelho específico.</summary>
public sealed class DeviceSettings
{
    public string? Nickname { get; set; }
    public DeviceCategory? Category { get; set; }
    public bool Hidden { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Nickname is null && Category is null && !Hidden;
}

/// <summary>
/// Persiste apelidos, tipos manuais e aparelhos ocultos em
/// %LOCALAPPDATA%\BluetoothPlus\settings.json (por usuário do Windows).
/// </summary>
public static class SettingsStore
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothPlus");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static readonly Data Current = Load();

    public static bool ShowHidden
    {
        get => Current.ShowHidden;
        set
        {
            Current.ShowHidden = value;
            Write();
        }
    }

    /// <summary>A tela de boas-vindas (créditos com contagem) já foi exibida a este usuário.</summary>
    public static bool WelcomeShown
    {
        get => Current.WelcomeShown;
        set
        {
            Current.WelcomeShown = value;
            Write();
        }
    }

    /// <summary>Alerta de bateria fraca usado quando o tipo não tem valor próprio.</summary>
    public const int DefaultBatteryAlert = 20;

    /// <summary>Porcentagem em que avisar bateria fraca para o tipo; 0 = sem aviso.</summary>
    public static int GetBatteryAlert(DeviceCategory category)
        => Current.BatteryAlerts.TryGetValue(category.ToString(), out var v) ? v : DefaultBatteryAlert;

    public static void SetBatteryAlerts(IEnumerable<KeyValuePair<DeviceCategory, int>> alerts)
    {
        foreach (var (category, value) in alerts)
        {
            // Só guarda o que difere do padrão, para mudanças futuras no padrão valerem.
            if (value == DefaultBatteryAlert) Current.BatteryAlerts.Remove(category.ToString());
            else Current.BatteryAlerts[category.ToString()] = value;
        }
        Write();
    }

    public static DeviceSettings? Get(string address)
        => Current.Devices.TryGetValue(address.ToLowerInvariant(), out var s) ? s : null;

    public static void Set(string address, DeviceSettings settings)
    {
        var key = address.ToLowerInvariant();
        if (settings.IsEmpty) Current.Devices.Remove(key);
        else Current.Devices[key] = settings;
        Write();
    }

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new Data();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Arquivo corrompido ou inacessível: começa do zero em vez de impedir o app de abrir.
        }
        return new Data();
    }

    private static void Write()
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed class Data
    {
        public bool ShowHidden { get; set; }
        public bool WelcomeShown { get; set; }
        public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

        /// <summary>Tipo (nome do enum) → porcentagem do alerta de bateria fraca.</summary>
        public Dictionary<string, int> BatteryAlerts { get; set; } = new();
    }
}
