using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using BluetoothPlus.Models;
using BluetoothPlus.Views;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace BluetoothPlus.Services;

/// <summary>
/// Descoberta e controle de dispositivos Bluetooth.
///
/// Usa o mesmo mecanismo do painel "Bluetooth e dispositivos" do Windows:
/// um DeviceWatcher sobre AssociationEndpoints (AEP) filtrado pelos
/// protocolos Bluetooth Clássico e LE. Em paralelo, um watcher de anúncios
/// BLE fornece RSSI em tempo real, que o painel do Windows não exibe.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    private const string ClassicProtocolId = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LeProtocolId = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    private const string PropAddress = "System.Devices.Aep.DeviceAddress";
    private const string PropConnected = "System.Devices.Aep.IsConnected";
    private const string PropPaired = "System.Devices.Aep.IsPaired";
    private const string PropSignal = "System.Devices.Aep.SignalStrength";
    private const string PropProtocol = "System.Devices.Aep.ProtocolId";
    private const string PropContainer = "System.Devices.Aep.ContainerId";
    private const string PropCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string PropCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string PropLeAppearance = "System.Devices.Aep.Bluetooth.Le.Appearance";

    /// <summary>Tipo de seção de anúncio BLE que carrega o GAP Appearance.</summary>
    private const byte AdTypeAppearance = 0x19;

    /// <summary>
    /// DEVPKEY_Bluetooth_Battery: nível de bateria que o Windows publica nos nós
    /// PnP de dispositivos Clássicos (fones via HFP). É o valor que o painel do
    /// Windows mostra ao lado do nome do fone.
    /// </summary>
    private const string PropPnpBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private static readonly string[] BaseProps = { PropAddress, PropConnected, PropPaired, PropSignal, PropProtocol, PropContainer };
    private static readonly string[] RequestedProps = [.. BaseProps, PropCodMajor, PropCodMinor, PropLeAppearance];

    /// <summary>Dispositivos não pareados somem da lista após esse tempo sem sinal.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly Dispatcher _ui;
    private readonly Dictionary<string, DeviceItem> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addressById = new();
    private readonly Dictionary<string, DeviceInformation> _infoById = new();
    private readonly DispatcherTimer _pruneTimer;
    private readonly DispatcherTimer _batteryTimer;
    private DeviceWatcher? _deviceWatcher;
    private BluetoothLEAdvertisementWatcher? _adWatcher;

    public BluetoothService(Dispatcher ui)
    {
        _ui = ui;
        _pruneTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Prune(), ui);
        _batteryTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await RefreshBatteriesAsync(), ui);
    }

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public bool IsScanning { get; private set; }

    /// <summary>Um dispositivo pareado conectou (true) ou desconectou (false).</summary>
    public event Action<DeviceItem, bool>? ConnectionChanged;

    /// <summary>Erro ao processar um evento do Windows; a busca continua.</summary>
    public event Action<string>? Warning;

    /// <summary>Terminou uma rodada de leitura de bateria dos aparelhos conectados.</summary>
    public event Action? BatteriesRefreshed;

    public void Start()
    {
        if (IsScanning) return;

        var aqs = $"System.Devices.Aep.ProtocolId:=\"{ClassicProtocolId}\" OR System.Devices.Aep.ProtocolId:=\"{LeProtocolId}\"";
        try
        {
            _deviceWatcher = DeviceInformation.CreateWatcher(aqs, RequestedProps, DeviceInformationKind.AssociationEndpoint);
        }
        catch (ArgumentException)
        {
            // Windows sem as propriedades de classe/aparência: o tipo vem só do anúncio BLE e do nome.
            _deviceWatcher = DeviceInformation.CreateWatcher(aqs, BaseProps, DeviceInformationKind.AssociationEndpoint);
        }
        _deviceWatcher.Added += (_, info) => _ui.BeginInvoke(() => Safe(() => OnAdded(info)));
        _deviceWatcher.Updated += (_, update) => _ui.BeginInvoke(() => Safe(() => OnUpdated(update)));
        _deviceWatcher.Removed += (_, update) => _ui.BeginInvoke(() => Safe(() => OnRemoved(update)));
        // Assim que o Windows termina de listar os aparelhos, lê as baterias na hora
        // (sem esperar o ciclo de 30 s) — é o que permite avisar logo ao abrir o app.
        _deviceWatcher.EnumerationCompleted += (_, _) => _ui.BeginInvoke(() => { _ = RefreshBatteriesAsync(); });
        _deviceWatcher.Start();

        _adWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _adWatcher.Received += (_, args) =>
        {
            var address = FormatAddress(args.BluetoothAddress);
            var name = args.Advertisement.LocalName;
            var rssi = (int)args.RawSignalStrengthInDBm;
            var appearance = ReadAppearance(args.Advertisement);
            _ui.BeginInvoke(() => Safe(() => OnAdvertisement(address, name, rssi, appearance)));
        };
        _adWatcher.Start();

        _pruneTimer.Start();
        _batteryTimer.Start();
        IsScanning = true;
    }

    public void Stop()
    {
        if (!IsScanning) return;
        _pruneTimer.Stop();
        _batteryTimer.Stop();

        if (_deviceWatcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            _deviceWatcher.Stop();
        _deviceWatcher = null;

        _adWatcher?.Stop();
        _adWatcher = null;

        IsScanning = false;
    }

    // ---------------------------------------------------------------- eventos

    // Um mesmo aparelho pode ter VÁRIAS entradas no Windows com o mesmo endereço
    // (ex.: uma Clássica e uma BLE). Cada atualização precisa ir para a entrada
    // com o mesmo Id — DeviceInformation.Update() lança "the identity of
    // updateInfo does not match" se aplicada à entrada errada.

    private void OnAdded(DeviceInformation info)
    {
        var address = ReadProp<string>(info, PropAddress)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(address)) return;

        _addressById[info.Id] = address;
        _infoById[info.Id] = info;
        var item = GetOrCreate(address);
        ApplyEntries(item, initial: true);
        if (item.IsPaired) _ = RefreshBatteryAsync(item);
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_infoById.TryGetValue(update.Id, out var info)) return;
        if (!_byAddress.TryGetValue(_addressById[update.Id], out var item)) return;

        info.Update(update);
        ApplyEntries(item, initial: false);
    }

    private void OnRemoved(DeviceInformationUpdate update)
    {
        _infoById.Remove(update.Id);
        if (!_addressById.Remove(update.Id, out var address)) return;
        if (!_byAddress.TryGetValue(address, out var item)) return;

        ApplyEntries(item, initial: false);
        if (item.Info is null && !item.IsPaired && DateTime.Now - item.LastSeen > TimeSpan.FromSeconds(5))
            RemoveItem(item);
    }

    private List<DeviceInformation> EntriesFor(string address)
        => _addressById.Where(kv => kv.Value == address).Select(kv => _infoById[kv.Key]).ToList();

    private void OnAdvertisement(string address, string name, int rssi, int? appearance)
    {
        var item = GetOrCreate(address);
        if (item.Info is null) item.Kind = DeviceKind.LowEnergy;
        if (!string.IsNullOrWhiteSpace(name)) item.Name = name;
        if (appearance is int a) item.SetReportedCategory(DeviceClassifier.FromAppearance(a));
        item.AddRssi(rssi);
    }

    /// <summary>
    /// Combina todas as entradas do aparelho num estado só: pareado/conectado se
    /// QUALQUER entrada estiver, sinal mais forte entre elas. A entrada pareada
    /// (se houver) é a principal — é ela que se usa para parear/remover.
    /// </summary>
    private void ApplyEntries(DeviceItem item, bool initial)
    {
        var entries = EntriesFor(item.Address);
        if (entries.Count == 0)
        {
            item.Info = null;
            return;
        }

        bool IsPaired(DeviceInformation i) => ReadProp<bool?>(i, PropPaired) ?? i.Pairing.IsPaired;
        var primary = entries.FirstOrDefault(IsPaired) ?? entries[0];
        item.Info = primary;

        var name = new[] { primary }.Concat(entries).Select(i => i.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (name is not null) item.Name = name;

        if (ReadProp<Guid?>(primary, PropProtocol) is Guid g)
            item.Kind = g == Guid.Parse(LeProtocolId) ? DeviceKind.LowEnergy : DeviceKind.Classic;
        if (entries.Select(i => ReadProp<Guid?>(i, PropContainer)).FirstOrDefault(c => c is not null) is Guid container)
            item.ContainerId = container;

        // Class of Device (Clássico) é mais específico que o Appearance (BLE).
        var reported = entries
            .Select(i => ReadInt(i, PropCodMajor) is int major && major != 0
                ? DeviceClassifier.FromClassOfDevice(major, ReadInt(i, PropCodMinor) ?? 0)
                : DeviceCategory.Other)
            .Concat(entries.Select(i => ReadInt(i, PropLeAppearance) is int a && a != 0
                ? DeviceClassifier.FromAppearance(a)
                : DeviceCategory.Other))
            .FirstOrDefault(c => c != DeviceCategory.Other);
        item.SetReportedCategory(reported);

        item.IsPaired = entries.Any(IsPaired);

        var wasConnected = item.IsConnected;
        item.IsConnected = entries.Any(i => ReadProp<bool?>(i, PropConnected) == true);
        // Na enumeração inicial tudo "muda" de false para o estado real — não é evento.
        if (!initial && item.IsPaired && wasConnected != item.IsConnected)
        {
            ConnectionChanged?.Invoke(item, item.IsConnected);
            if (item.IsConnected) _ = RefreshBatteryAsync(item);
        }

        var signals = entries.Select(i => ReadProp<int?>(i, PropSignal)).OfType<int>().ToList();
        if (signals.Count > 0) item.AddRssi(signals.Max());
        else item.Touch();
    }

    private void Prune()
    {
        var now = DateTime.Now;
        foreach (var item in Devices.ToList())
        {
            if (!item.IsPaired && item.Info is null && now - item.LastSeen > StaleAfter)
                RemoveItem(item);
        }
    }

    // ---------------------------------------------------------------- ações

    public async Task<string> PairAsync(DeviceItem item)
    {
        var info = item.Info ?? await ResolveInfoAsync(item);
        if (info is null) return "O Windows ainda não reconheceu esse dispositivo. Aguarde alguns segundos e tente de novo.";
        if (info.Pairing.IsPaired) return "Já está pareado.";
        if (!info.Pairing.CanPair) return "Esse dispositivo não aceita pareamento agora (coloque-o em modo de pareamento).";

        var custom = info.Pairing.Custom;
        custom.PairingRequested += OnPairingRequested;
        try
        {
            var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin
                      | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin;
            var result = await custom.PairAsync(kinds, DevicePairingProtectionLevel.Default);
            if (result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                item.IsPaired = true;
            return Describe(result.Status);
        }
        finally
        {
            custom.PairingRequested -= OnPairingRequested;
        }
    }

    public async Task<string> UnpairAsync(DeviceItem item)
    {
        var info = item.Info ?? await ResolveInfoAsync(item);
        if (info is null || !info.Pairing.IsPaired) return "Não está pareado.";

        var result = await info.Pairing.UnpairAsync();
        if (result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired)
        {
            item.IsPaired = false;
            item.IsConnected = false;
            return "Dispositivo removido.";
        }
        return $"Falha ao remover: {result.Status}";
    }

    /// <summary>
    /// Lê a bateria: primeiro o valor que o Windows já publica (fones Clássicos
    /// e alguns BLE pareados); se não houver, consulta o GATT Battery Service
    /// (0x180F) diretamente — isso pode conectar ao dispositivo BLE.
    /// </summary>
    public static async Task<int?> ReadBatteryAsync(DeviceItem item)
    {
        if (await ReadPnpBatteryAsync(item) is int pnp) return pnp;
        if (item.Kind != DeviceKind.LowEnergy) return null;
        return await ReadGattBatteryAsync(item);
    }

    private static async Task<int?> ReadPnpBatteryAsync(DeviceItem item)
    {
        if (item.ContainerId is not Guid container) return null;

        var aqs = $"System.Devices.ContainerId:=\"{{{container}}}\"";
        var nodes = await DeviceInformation.FindAllAsync(aqs, new[] { PropPnpBattery }, DeviceInformationKind.Device);
        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(PropPnpBattery, out var value) && value is byte level)
                return level;
        }
        return null;
    }

    /// <summary>
    /// Leitura automática em segundo plano. O valor publicado pelo Windows (PnP)
    /// cobre fones e caixas Clássicos; teclados, mouses e controles BLE só
    /// informam a bateria pelo GATT — consultado apenas se o aparelho já está
    /// conectado, para não forçar conexão com aparelhos desligados.
    /// </summary>
    private async Task RefreshBatteryAsync(DeviceItem item)
    {
        try
        {
            var level = await ReadPnpBatteryAsync(item);
            if (level is null && item.Kind == DeviceKind.LowEnergy && item.IsConnected)
                level = await ReadGattBatteryAsync(item);
            if (level is not null) item.Battery = level;
        }
        catch
        {
            // Leitura oportunista; o botão "Bateria" mostra o erro quando o usuário pede.
        }
    }

    private async Task RefreshBatteriesAsync()
    {
        foreach (var item in Devices.Where(d => d.IsPaired && d.IsConnected).ToList())
            await RefreshBatteryAsync(item);
        BatteriesRefreshed?.Invoke();
    }

    private static async Task<int?> ReadGattBatteryAsync(DeviceItem item)
    {
        if (!TryParseAddress(item.Address, out var address)) return null;

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return null;

        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return null;

        try
        {
            var chars = await services.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;

            var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success) return null;

            return DataReader.FromBuffer(read.Value).ReadByte();
        }
        finally
        {
            foreach (var s in services.Services) s.Dispose();
        }
    }

    /// <summary>Rádio Bluetooth do PC (para ligar/desligar), ou null se inacessível.</summary>
    public static async Task<Radio?> GetRadioAsync()
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
        var radios = await Radio.GetRadiosAsync();
        return radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
    }

    // ---------------------------------------------------------------- helpers

    private void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    args.Accept();
                    break;

                case DevicePairingKinds.DisplayPin:
                    args.Accept();
                    _ui.BeginInvoke(() => MessageBox.Show($"Digite este PIN no dispositivo:\n\n{args.Pin}",
                        "Bluetooth+ — pareamento", MessageBoxButton.OK, MessageBoxImage.Information));
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                    var ok = _ui.Invoke(() => MessageBox.Show($"O código {args.Pin} aparece no dispositivo?",
                        "Bluetooth+ — pareamento", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
                    if (ok) args.Accept();
                    break;

                case DevicePairingKinds.ProvidePin:
                    var pin = _ui.Invoke(() => PinDialog.Ask(Application.Current.MainWindow));
                    if (!string.IsNullOrEmpty(pin)) args.Accept(pin);
                    break;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async Task<DeviceInformation?> ResolveInfoAsync(DeviceItem item)
    {
        if (!TryParseAddress(item.Address, out var address)) return null;
        if (item.Kind == DeviceKind.LowEnergy)
        {
            using var le = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            return le?.DeviceInformation;
        }
        using var classic = await BluetoothDevice.FromBluetoothAddressAsync(address);
        return classic?.DeviceInformation;
    }

    /// <summary>
    /// Eventos de descoberta chegam continuamente: um erro num deles não deve
    /// abrir uma janela de erro nem interromper a busca — vira aviso na barra de status.
    /// </summary>
    private void Safe(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"Aviso: {ex.Message}");
        }
    }

    private DeviceItem GetOrCreate(string address)
    {
        if (_byAddress.TryGetValue(address, out var item)) return item;
        item = new DeviceItem(address);
        _byAddress[address] = item;
        Devices.Add(item);
        return item;
    }

    private void RemoveItem(DeviceItem item)
    {
        _byAddress.Remove(item.Address);
        Devices.Remove(item);
    }

    private static T? ReadProp<T>(DeviceInformation info, string key)
        => info.Properties.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>Lê propriedades numéricas cujo tipo varia (byte/UInt16/UInt32) conforme a versão do Windows.</summary>
    private static int? ReadInt(DeviceInformation info, string key)
        => info.Properties.TryGetValue(key, out var value) && value is IConvertible c ? c.ToInt32(CultureInfo.InvariantCulture) : null;

    private static int? ReadAppearance(BluetoothLEAdvertisement ad)
    {
        foreach (var section in ad.DataSections)
        {
            if (section.DataType != AdTypeAppearance || section.Data.Length < 2) continue;
            var reader = DataReader.FromBuffer(section.Data);
            reader.ByteOrder = ByteOrder.LittleEndian;
            return reader.ReadUInt16();
        }
        return null;
    }

    private static string FormatAddress(ulong address)
        => string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("x2")));

    private static bool TryParseAddress(string address, out ulong value)
        => ulong.TryParse(address.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static string Describe(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.Paired => "Pareado com sucesso.",
        DevicePairingResultStatus.AlreadyPaired => "Já estava pareado.",
        DevicePairingResultStatus.NotReadyToPair => "O dispositivo não está pronto — coloque-o em modo de pareamento.",
        DevicePairingResultStatus.AuthenticationTimeout => "Tempo esgotado aguardando confirmação.",
        DevicePairingResultStatus.PairingCanceled => "Pareamento cancelado.",
        DevicePairingResultStatus.RejectedByHandler => "Pareamento recusado.",
        DevicePairingResultStatus.AuthenticationFailure => "Falha de autenticação (PIN incorreto?).",
        DevicePairingResultStatus.ConnectionRejected => "O dispositivo recusou a conexão.",
        _ => $"Não foi possível parear: {status}",
    };

    public void Dispose() => Stop();
}
