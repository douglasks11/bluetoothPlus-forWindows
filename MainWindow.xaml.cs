using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BluetoothPlus.Models;
using BluetoothPlus.Services;
using BluetoothPlus.Views;
using Windows.Devices.Radios;

namespace BluetoothPlus;

public partial class MainWindow : Window
{
    // Faixa de RSSI mapeada no radar/gráfico: -30 dBm (colado) a -100 dBm (limite).
    private const double RssiNear = -30;
    private const double RssiFar = -100;

    private readonly BluetoothService _bt;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _renderTimer;
    private readonly TrayIcon _tray;
    private readonly FontFamily _iconFont;
    private Radio? _radio;
    private readonly DispatcherTimer _trayTimer;
    private readonly HashSet<string> _lowBatteryNotified = new();
    private readonly DateTime _startedAt = DateTime.Now;

    // Na abertura, os avisos esperam a primeira leitura completa de baterias para
    // sair num aviso só (com fallback de tempo caso essa leitura não aconteça).
    private bool _firstBatteryReadDone;
    private static readonly TimeSpan FirstBatteryReadTimeout = TimeSpan.FromSeconds(20);
    private bool _exiting;
    private bool _trayHintShown;

    /// <summary>
    /// Depois de avisar, só avisa de novo quando a bateria subir esta margem acima
    /// do limite (recarregou) — evita repetir o aviso a cada oscilação de 1%.
    /// </summary>
    private const int LowBatteryRearmMargin = 10;

    public MainWindow()
    {
        InitializeComponent();
        _iconFont = (FontFamily)FindResource("IconFont");

        _bt = new BluetoothService(Dispatcher);
        _bt.ConnectionChanged += OnConnectionChanged;
        _bt.Warning += SetStatus;
        _bt.BatteriesRefreshed += () =>
        {
            _firstBatteryReadDone = true;
            UpdateTrayAndBatteryAlerts();
        };
        // Apelido, tipo manual e "oculto" salvos são aplicados assim que o aparelho aparece.
        _bt.Devices.CollectionChanged += (_, e) =>
        {
            foreach (DeviceItem d in e.NewItems ?? Array.Empty<DeviceItem>())
                d.Apply(SettingsStore.Get(d.Address));
        };

        _tray = new TrayIcon();

        _tray.OpenRequested += ShowFromTray;
        _tray.ScanToggleRequested += ToggleScan;
        _tray.ExitRequested += ExitApp;
        _tray.CreditsRequested += () =>
        {
            ShowFromTray();
            AboutWindow.ShowCredits(this);
        };

        // Pareados primeiro; dentro do grupo, conectados e depois por força de sinal.
        // Ordena por SignalLevel (0–4) e não por RSSI bruto para a lista não "dançar".
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_bt.Devices);
        // O WPF ordena os grupos pela ordem em que são criados; como os anúncios BLE
        // de aparelhos próximos costumam chegar antes da lista de pareados, a ordem
        // é fixada aqui para "Meus dispositivos" ficar sempre em cima.
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceItem.GroupLabel))
        {
            CustomSort = new GroupOrder(),
        });
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.IsPaired), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.IsConnected), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.SignalLevel), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.DisplayName), ListSortDirection.Ascending));
        _view.IsLiveSorting = true;
        _view.IsLiveGrouping = true;
        foreach (var p in new[] { nameof(DeviceItem.IsPaired), nameof(DeviceItem.IsConnected), nameof(DeviceItem.SignalLevel), nameof(DeviceItem.DisplayName) })
            _view.LiveSortingProperties.Add(p);
        _view.LiveGroupingProperties.Add(nameof(DeviceItem.GroupLabel));
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(DeviceItem.IsHidden), nameof(DeviceItem.IsUnnamed), nameof(DeviceItem.IsPaired) })
            _view.LiveFilteringProperties.Add(p);
        _view.Filter = MatchesFilter;
        ShowHiddenBox.IsChecked = SettingsStore.ShowHidden;
        DevicesList.ItemsSource = _view;

        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => Render(), Dispatcher);
        // Bandeja e alerta de bateria rodam mesmo com a janela oculta.
        _trayTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => UpdateTrayAndBatteryAlerts(), Dispatcher);
        _trayTimer.Start();

        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        ThemeManager.Changed += OnThemeChanged;
        UpdateThemeButton();
        Closing += OnClosing;
        // Não depende de Loaded: com --tray a janela começa oculta.
        InitializeAsync();
    }

    private DeviceItem? Selected => DevicesList.SelectedItem as DeviceItem;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _renderTimer.Stop();
        _trayTimer.Stop();
        _bt.Dispose();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        // Fechar a janela só esconde: o app segue na bandeja avisando conexões.
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _tray.Notify("Bluetooth+", "Continua rodando na bandeja. Clique com o botão direito no ícone para sair.");
            _trayHintShown = true;
        }
    }

    private void OnConnectionChanged(DeviceItem item, bool connected)
    {
        var text = connected
            ? item.Battery is int b ? $"Conectado · bateria {b}%" : "Conectado"
            : "Desconectado";
        _tray.Notify(item.DisplayName, $"{item.CategoryLabel} — {text}");
        SetStatus($"{item.DisplayName}: {text.ToLowerInvariant()}");
        UpdateTrayAndBatteryAlerts();
    }

    private void ToggleScan()
    {
        if (_bt.IsScanning)
        {
            _bt.Stop();
            SetStatus("Busca pausada.");
        }
        else
        {
            _bt.Start();
            SetStatus("Buscando dispositivos…");
        }
        UpdateScanButton();
        _tray.SetScanning(_bt.IsScanning);
    }

    private async void InitializeAsync()
    {
        _bt.Start();
        UpdateScanButton();
        _renderTimer.Start();
        SetStatus("Buscando dispositivos…");

        _radio = await BluetoothService.GetRadioAsync();
        if (_radio is null)
        {
            RadioLabel.Text = "Bluetooth indisponível";
            RadioToggle.IsEnabled = false;
            SetStatus("Nenhum adaptador Bluetooth encontrado ou acesso negado.");
            return;
        }
        _radio.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateRadioToggle);
        UpdateRadioToggle();
    }

    // ---------------------------------------------------------------- UI handlers

    private void ScanButton_Click(object sender, RoutedEventArgs e) => ToggleScan();

    private async void RadioToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_radio is null) return;
        RadioToggle.IsEnabled = false;
        var target = _radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        var result = await _radio.SetStateAsync(target);
        if (result != RadioAccessStatus.Allowed) SetStatus($"Não foi possível alterar o Bluetooth: {result}");
        RadioToggle.IsEnabled = true;
        UpdateRadioToggle();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        await RunAction($"Pareando com {item.DisplayName}…", () => _bt.PairAsync(item));
    }

    private async void UnpairButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var confirm = MessageBox.Show($"Remover \"{item.DisplayName}\" dos dispositivos pareados?", "Bluetooth+",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        await RunAction($"Removendo {item.DisplayName}…", () => _bt.UnpairAsync(item));
    }

    private async void BatteryButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        await RunAction("Lendo bateria…", async () =>
        {
            var level = await BluetoothService.ReadBatteryAsync(item);
            item.Battery = level;
            return level is int b ? $"Bateria: {b}%" : "Esse dispositivo não informa o nível de bateria.";
        });
    }

    private async Task RunAction(string busyMessage, Func<Task<string>> action)
    {
        SetActionsEnabled(false);
        SetStatus(busyMessage);
        try
        {
            SetStatus(await action());
        }
        catch (Exception ex)
        {
            SetStatus($"Erro: {ex.Message}");
        }
        finally
        {
            UpdateDetails();
        }
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        if (!IsVisible) return; // oculta na bandeja: não desenha à toa

        int total = _view.Count, paired = _bt.Devices.Count(d => d.IsPaired);
        CountText.Text = $"{total} dispositivo(s) · {paired} pareado(s)";

        int hidden = _bt.Devices.Count(IsHiddenByPreference);
        ShowHiddenBox.Content = $"Mostrar ocultos e sem nome ({hidden})";

        EmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = FilterBox.Text.Length > 0 ? "Nenhum dispositivo corresponde à busca."
            : hidden > 0 && ShowHiddenBox.IsChecked != true ? "Só há dispositivos ocultos por perto."
            : _bt.IsScanning ? "Procurando dispositivos por perto…" : "Busca pausada.";

        DrawRadar();
        DrawSignal();
        RefreshDetailStats();
    }

    private void DrawRadar()
    {
        var c = RadarCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 60 || h < 60) return;

        double cx = w / 2, cy = h / 2, radius = Math.Min(w, h) / 2 - 18;

        Add(c, new Ellipse { Width = radius * 2, Height = radius * 2, Fill = Res("RadarGlow") }, cx - radius, cy - radius);
        for (int i = 1; i <= 4; i++)
        {
            double r = radius * i / 4;
            Add(c, new Ellipse { Width = r * 2, Height = r * 2, Stroke = Res("RadarGrid"), StrokeThickness = 1 }, cx - r, cy - r);
            var dbm = RssiNear + (RssiFar - RssiNear) * i / 4;
            Add(c, Label($"{dbm:0} dBm", 10, Res("RadarLabel")), cx + 4, cy - r + 2);
        }

        // Varredura com rastro.
        if (_bt.IsScanning)
        {
            var accent = ((SolidColorBrush)Res("Accent")).Color;
            double sweep = DateTime.Now.TimeOfDay.TotalSeconds * 1.6 % (Math.PI * 2);
            for (int t = 0; t < 6; t++)
            {
                double a = sweep - t * 0.06;
                c.Children.Add(new Line
                {
                    X1 = cx, Y1 = cy,
                    X2 = cx + Math.Cos(a) * radius, Y2 = cy + Math.Sin(a) * radius,
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)(0x70 - t * 0x12), accent.R, accent.G, accent.B)),
                    StrokeThickness = 2,
                });
            }
        }

        // Centro = este PC.
        var pc = IconBadge("\uE7F8", 30, 14, Brushes.White, Res("Accent"), null);
        pc.ToolTip = "Este computador";
        Add(c, pc, cx - 15, cy - 15);

        var selected = Selected;
        foreach (DeviceItem d in _view)
        {
            if (d.Rssi is not int rssi) continue;
            double dist = Math.Max(Normalize(rssi) * radius, 24);
            double x = cx + Math.Cos(d.Angle) * dist;
            double y = cy + Math.Sin(d.Angle) * dist;

            bool isSel = ReferenceEquals(d, selected);
            double size = isSel ? 34 : 26;
            double age = (DateTime.Now - d.LastSeen).TotalSeconds;

            var badge = IconBadge(d.CategoryGlyph, size, isSel ? 16 : 13, d.CategoryBrush, Res("Surface"),
                isSel ? Res("Text") : d.CategoryBrush);
            badge.Opacity = Math.Clamp(1.0 - age / 30.0, 0.3, 1.0);
            badge.Cursor = Cursors.Hand;
            badge.ToolTip = $"{d.DisplayName}\n{d.CategoryLabel} · {d.RssiLabel} · {d.StatusLabel}";
            badge.Tag = d;
            badge.MouseLeftButtonDown += (s, _) =>
            {
                DevicesList.SelectedItem = ((FrameworkElement)s).Tag;
                DevicesList.ScrollIntoView(DevicesList.SelectedItem);
            };
            Add(c, badge, x - size / 2, y - size / 2);

            if (d.IsPaired)
                Add(c, new Ellipse { Width = 8, Height = 8, Fill = Res("Gold") }, x + size / 2 - 7, y - size / 2 - 1);

            if (isSel || d.IsPaired || !d.IsUnnamed)
            {
                // Fundo atrás do nome para ele não se misturar com os anéis do radar.
                var label = new Border
                {
                    Background = Res("RadarNameBackdrop"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 2),
                    IsHitTestVisible = false,
                    Child = Label(Truncate(d.DisplayName, 16), isSel ? 12.5 : 11.5, Res("RadarName")),
                };
                Add(c, label, x + size / 2 + 4, y - 10);
            }
        }
    }

    private void DrawSignal()
    {
        var c = SignalCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 20 || h < 20 || Selected is not { } item) return;

        foreach (var dbm in new[] { -40, -60, -80 })
        {
            double y = Normalize(dbm) * h;
            c.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Res("RadarGrid"), StrokeDashArray = new DoubleCollection { 3, 3 } });
            Add(c, Label($"{dbm}", 10, Res("RadarLabel")), 6, y - 15);
        }

        if (item.RssiHistory.Count < 2)
        {
            Add(c, Label("Aguardando leituras de sinal…", 12, Res("RadarLabel")), w / 2 - 85, h / 2 - 8);
            return;
        }

        var samples = item.RssiHistory.ToArray();
        double step = w / (DeviceItem.HistorySize - 1);
        double x0 = w - step * (samples.Length - 1);

        var line = new PointCollection();
        for (int i = 0; i < samples.Length; i++)
            line.Add(new Point(x0 + i * step, Normalize(samples[i]) * h));

        // Área preenchida sob a curva, na cor da categoria.
        var area = new PointCollection(line) { new Point(w, h), new Point(x0, h) };
        var color = ((SolidColorBrush)item.CategoryBrush).Color;
        c.Children.Add(new Polygon
        {
            Points = area,
            Fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B),
                                           Color.FromArgb(0x00, color.R, color.G, color.B), 90),
        });
        c.Children.Add(new Polyline
        {
            Points = line,
            Stroke = item.CategoryBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    // ---------------------------------------------------------------- detalhes

    private void UpdateDetails()
    {
        var item = Selected;
        DetailPanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DetailEmpty.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) return;

        PairButton.IsEnabled = !item.IsPaired;
        UnpairButton.IsEnabled = item.IsPaired;
        BatteryButton.IsEnabled = item.IsPaired || item.Kind == DeviceKind.LowEnergy;
        RefreshDetailStats();
    }

    private void RefreshDetailStats()
    {
        if (Selected is not { } item) return;
        DetailIconTile.Background = Res("Surface");
        DetailGlyph.Text = item.CategoryGlyph;
        DetailGlyph.Foreground = item.CategoryBrush;
        DetailName.Text = item.DisplayName;
        DetailSubtitle.Text = item.Subtitle;
        DetailAddress.Text = $"Endereço {item.Address.ToUpperInvariant()}";

        StatSignal.Text = item.RssiLabel;
        StatBattery.Text = item.BatteryLabel;
        StatBatteryGlyph.Text = item.BatteryGlyph;
        StatStatus.Text = item.StatusLabel;
        StatStatusDot.Fill = Res(item.IsConnected ? "Ok" : item.IsPaired ? "Gold" : "Neutral");
    }

    // ---------------------------------------------------------------- helpers

    private void SetActionsEnabled(bool enabled)
    {
        PairButton.IsEnabled = UnpairButton.IsEnabled = BatteryButton.IsEnabled = enabled;
    }

    private void UpdateScanButton()
    {
        ScanLabel.Text = _bt.IsScanning ? "Pausar busca" : "Buscar";
        ScanGlyph.Text = _bt.IsScanning ? "\uE769" : "\uE768";
    }

    private void UpdateRadioToggle()
    {
        if (_radio is null) return;
        RadioLabel.Text = _radio.State == RadioState.On ? "Bluetooth ligado" : "Bluetooth desligado";
    }

    private bool MatchesFilter(object o)
    {
        var d = (DeviceItem)o;
        if (ShowHiddenBox.IsChecked != true && IsHiddenByPreference(d)) return false;

        var q = FilterBox.Text.Trim();
        if (q.Length == 0) return true;
        return d.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || d.CategoryLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
            || d.Address.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Oculto pelo usuário, ou sem nome e não pareado (TVs de vizinhos, beacons…).</summary>
    private static bool IsHiddenByPreference(DeviceItem d) => d.IsHidden || (d.IsUnnamed && !d.IsPaired);

    private void ShowHiddenBox_Changed(object sender, RoutedEventArgs e)
    {
        SettingsStore.ShowHidden = ShowHiddenBox.IsChecked == true;
        _view.Refresh();
    }

    private void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (!_bt.IsScanning) ToggleScan(); // o medidor depende de leituras contínuas de sinal
        FindDeviceWindow.Open(item, this);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (EditDeviceDialog.Edit(item, this))
        {
            SetStatus(item.IsHidden ? $"{item.DisplayName} foi ocultado." : $"{item.DisplayName} atualizado.");
            UpdateDetails();
            UpdateTrayAndBatteryAlerts();
        }
    }

    // ---------------------------------------------------------------- bandeja e bateria

    private void UpdateTrayAndBatteryAlerts()
    {
        var connected = _bt.Devices.Where(d => d.IsConnected).OrderBy(d => d.DisplayName).ToList();
        _tray.SetConnectedDevices(connected
            .Select(d => d.Battery is int b ? $"{d.DisplayName} · {b}%" : d.DisplayName)
            .ToList());

        if (!_firstBatteryReadDone && DateTime.Now - _startedAt < FirstBatteryReadTimeout) return;

        // Junta todos os que ficaram abaixo do limite nesta checagem: o ícone da
        // bandeja tem um único balão, e avisos seguidos se substituiriam.
        var newlyLow = new List<(DeviceItem Device, int Level)>();
        foreach (var d in connected)
        {
            if (d.Battery is not int level) continue;
            int alertAt = SettingsStore.GetBatteryAlert(d.Category); // 0 = aviso desligado para o tipo
            if (alertAt > 0 && level <= alertAt && _lowBatteryNotified.Add(d.Address))
                newlyLow.Add((d, level));
            else if (level >= alertAt + LowBatteryRearmMargin)
                _lowBatteryNotified.Remove(d.Address);
        }
        if (newlyLow.Count == 0) return;

        var summary = string.Join(" · ", newlyLow.OrderBy(x => x.Level).Select(x => $"{x.Device.DisplayName} {x.Level}%"));
        if (newlyLow.Count == 1)
            _tray.Notify("Bateria fraca", $"{newlyLow[0].Device.DisplayName} está com {newlyLow[0].Level}% de bateria.");
        else
            _tray.Notify($"Bateria fraca em {newlyLow.Count} dispositivos", summary);
        SetStatus($"Aviso de bateria fraca: {summary}");
    }

    private void CreditsButton_Click(object sender, RoutedEventArgs e) => AboutWindow.ShowCredits(this);

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.Open(this, () => _tray.Notify("Bateria fraca", "Aviso de teste do Bluetooth+. Se você está vendo isto, os avisos funcionam.")))
        {
            SetStatus("Configurações salvas.");
            UpdateTrayAndBatteryAlerts();
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Círculo com o ícone da categoria, usado no radar.</summary>
    private Grid IconBadge(string glyph, double size, double fontSize, Brush foreground, Brush fill, Brush? stroke)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Fill = Res("Panel") });
        g.Children.Add(new Ellipse { Fill = fill, Stroke = stroke, StrokeThickness = stroke is null ? 0 : 1.5 });
        g.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = _iconFont,
            FontSize = fontSize,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    /// <summary>0 = perto (RssiNear) … 1 = longe (RssiFar).</summary>
    private static double Normalize(double rssi) => Math.Clamp((RssiNear - rssi) / (RssiNear - RssiFar), 0, 1);

    private static TextBlock Label(string text, double size, Brush brush) => new() { Text = text, FontSize = size, Foreground = brush };

    private static void Add(Canvas c, UIElement el, double left, double top)
    {
        Canvas.SetLeft(el, left);
        Canvas.SetTop(el, top);
        c.Children.Add(el);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>"Meus dispositivos" antes de "Por perto".</summary>
    private sealed class GroupOrder : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));

        private static int Rank(object? group)
            => (group as CollectionViewGroup)?.Name as string == DeviceItem.PairedGroup ? 0 : 1;
    }

    /// <summary>Cor do tema atual (Themes/Dark.xaml ou Themes/Light.xaml).</summary>
    private Brush Res(string key) => (Brush)FindResource(key);

    // ---------------------------------------------------------------- tema

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Mode = ThemeManager.Mode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        UpdateThemeButton();
    }

    private void OnThemeChanged()
    {
        foreach (var d in _bt.Devices) d.NotifyThemeChanged();
        ApplyTitleBarTheme();
        UpdateThemeButton();
        RefreshDetailStats();
    }

    private void UpdateThemeButton()
    {
        (ThemeGlyph.Text, ThemeLabel.Text) = ThemeManager.Mode switch
        {
            ThemeMode.Light => ("\uE706", "Claro"),
            ThemeMode.Dark => ("\uE708", "Escuro"),
            _ => ("\uE770", "Sistema"),
        };
        ThemeButton.ToolTip = "Tema: clique para alternar entre Sistema, Claro e Escuro";
    }

    // Barra de título clara/escura conforme o tema (Windows 10 20H1+ / Windows 11).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const int DwmwaUseImmersiveDarkMode = 20;
        int dark = ThemeManager.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }
}
