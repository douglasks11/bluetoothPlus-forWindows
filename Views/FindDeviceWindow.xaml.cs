using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BluetoothPlus.Models;

namespace BluetoothPlus.Views;

/// <summary>
/// Medidor "quente/frio" para achar um aparelho perdido pela intensidade do
/// sinal. O RSSI oscila muito, por isso é suavizado (média móvel exponencial)
/// e a tendência compara a média atual com a de ~2 s atrás.
/// </summary>
public partial class FindDeviceWindow : Window
{
    private const double RssiFar = -95;
    private const double RssiNear = -45;
    private const double Smoothing = 0.3;
    private static readonly TimeSpan SignalLost = TimeSpan.FromSeconds(6);

    // Arco do medidor: 270°, começando embaixo à esquerda.
    private const double ArcStart = 135;
    private const double ArcSweep = 270;

    private readonly DeviceItem _item;
    private readonly DispatcherTimer _timer;
    private readonly Queue<(DateTime At, double Value)> _trail = new();
    private readonly Thread _beeper;
    private long _lastSample = -1;
    private double? _smoothed;

    // Lidos pela thread do bipe.
    private volatile bool _closed;
    private volatile bool _beepOn;
    private volatile int _beepIntervalMs = 1500;

    private FindDeviceWindow(DeviceItem item)
    {
        InitializeComponent();
        _item = item;
        Title = $"Encontrar {item.DisplayName}";

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
        _beeper = new Thread(BeepLoop) { IsBackground = true, Name = "FindBeeper" };

        Loaded += (_, _) =>
        {
            _timer.Start();
            _beeper.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    public static void Open(DeviceItem item, Window owner)
    {
        // Uma janela por aparelho: reabrir só traz a existente para frente.
        var existing = owner.OwnedWindows.OfType<FindDeviceWindow>().FirstOrDefault(w => w._item == item);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        new FindDeviceWindow(item) { Owner = owner }.Show();
    }

    private void Tick()
    {
        DeviceGlyph.Text = _item.CategoryGlyph;
        DeviceGlyph.Foreground = _item.CategoryBrush;
        DeviceName.Text = _item.DisplayName;
        DeviceSubtitle.Text = _item.CategoryLabel;

        if (_item.SampleCount != _lastSample && _item.Rssi is int rssi)
        {
            _lastSample = _item.SampleCount;
            _smoothed = _smoothed is double s ? s + Smoothing * (rssi - s) : rssi;
        }

        var age = DateTime.Now - _item.LastSeen;
        bool hasSignal = _smoothed is not null && age < SignalLost;
        double proximity = hasSignal ? Math.Clamp((_smoothed!.Value - RssiFar) / (RssiNear - RssiFar), 0, 1) : 0;

        // IsVisible: a janela some junto com a principal quando ela vai para a bandeja.
        _beepOn = hasSignal && IsVisible && SoundBox.IsChecked == true;
        _beepIntervalMs = (int)(1400 - proximity * 1250); // 1,4 s (longe) → 0,15 s (colado)

        UpdateTexts(hasSignal, proximity, age);
        DrawGauge(hasSignal ? proximity : 0, hasSignal);
    }

    private void UpdateTexts(bool hasSignal, double proximity, TimeSpan age)
    {
        if (!hasSignal)
        {
            ProximityText.Text = "Sem sinal";
            TrendText.Text = "Mova-se pelo ambiente";
            TrendText.Foreground = Res("TextDim");
            ReadingText.Text = _smoothed is null ? "Aguardando a primeira leitura…" : $"Última leitura há {age.TotalSeconds:0} s";
            _trail.Clear();
            return;
        }

        ProximityText.Text = proximity switch
        {
            >= 0.8 => "Muito perto!",
            >= 0.55 => "Perto",
            >= 0.3 => "Longe",
            _ => "Muito longe",
        };

        // Tendência: média atual × média de ~2 s atrás.
        var now = DateTime.Now;
        _trail.Enqueue((now, _smoothed!.Value));
        while (_trail.Count > 0 && now - _trail.Peek().At > TimeSpan.FromSeconds(2)) _trail.Dequeue();
        double delta = _smoothed.Value - _trail.Peek().Value;
        (TrendText.Text, TrendText.Foreground) = delta switch
        {
            > 2 => ("↑ Esquentando", Res("Ok")),
            < -2 => ("↓ Esfriando", Res("Gold")),
            _ => ("Estável", Res("TextDim")),
        };

        ReadingText.Text = $"{_smoothed.Value:0} dBm · leitura há {age.TotalSeconds:0.0} s";
    }

    private void DrawGauge(double value, bool active)
    {
        var c = GaugeCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 50 || h < 50) return;

        double r = Math.Min(w, h) / 2 - 14, cx = w / 2, cy = h / 2;
        c.Children.Add(Arc(cx, cy, r, ArcStart, ArcSweep, Res("Surface"), 18));
        if (active && value > 0.005)
            c.Children.Add(Arc(cx, cy, r, ArcStart, ArcSweep * value, new SolidColorBrush(HeatColor(value)), 18));
    }

    /// <summary>Azul (frio) → âmbar → vermelho (quente).</summary>
    private static Color HeatColor(double t)
    {
        static Color Lerp(Color a, Color b, double k) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

        var cold = Color.FromRgb(0x3B, 0x82, 0xF6);
        var warm = Color.FromRgb(0xF5, 0x9E, 0x0B);
        var hot = Color.FromRgb(0xEF, 0x44, 0x44);
        return t < 0.5 ? Lerp(cold, warm, t * 2) : Lerp(warm, hot, (t - 0.5) * 2);
    }

    private static Path Arc(double cx, double cy, double r, double startDeg, double sweepDeg, Brush stroke, double thickness)
    {
        static Point At(double cx, double cy, double r, double deg)
            => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));

        var figure = new PathFigure { StartPoint = At(cx, cy, r, startDeg) };
        figure.Segments.Add(new ArcSegment(At(cx, cy, r, startDeg + sweepDeg), new Size(r, r), 0,
            sweepDeg > 180, SweepDirection.Clockwise, true));
        return new Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    // Bipe pelo alto-falante padrão; roda numa thread própria porque Beep() bloqueia.
    [DllImport("kernel32.dll")]
    private static extern bool Beep(uint frequency, uint durationMs);

    private void BeepLoop()
    {
        while (!_closed)
        {
            if (_beepOn)
            {
                Beep(880, 70);
                Thread.Sleep(_beepIntervalMs);
            }
            else
            {
                Thread.Sleep(200);
            }
        }
    }
}
