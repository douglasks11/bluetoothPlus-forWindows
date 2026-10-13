using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace BluetoothPlus.Views;

/// <summary>
/// Créditos. Também serve de boas-vindas na primeira execução: nesse modo o
/// botão só libera após uma contagem de 5 s.
/// </summary>
public partial class AboutWindow : Window
{
    private const int WelcomeSeconds = 5;

    private readonly DispatcherTimer? _timer;
    private int _remaining;

    private AboutWindow(bool welcome)
    {
        InitializeComponent();
        VersionText.Text = $"versão {AppInfo.Version}";
        AuthorText.Text = $"Feito por {AppInfo.Author} :)";
        GitHubText.Text = AppInfo.GitHubUrl.Replace("https://", "");

        if (!welcome)
        {
            CloseButton.Content = "Fechar";
            return;
        }

        Title = "Bem-vindo ao Bluetooth+";
        Heading.Text = "Bem-vindo ao Bluetooth+";
        _remaining = WelcomeSeconds;
        CloseButton.IsEnabled = false;
        UpdateCountdown();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Loaded += (_, _) => _timer.Start();
    }

    public static void ShowCredits(Window owner) => new AboutWindow(welcome: false) { Owner = owner }.ShowDialog();

    public static void ShowWelcome(Window owner) => new AboutWindow(welcome: true) { Owner = owner }.ShowDialog();

    private void Tick()
    {
        _remaining--;
        UpdateCountdown();
        if (_remaining > 0) return;
        _timer!.Stop();
        CloseButton.IsEnabled = true;
    }

    private void UpdateCountdown()
        => CloseButton.Content = _remaining > 0 ? $"Começar ({_remaining})" : "Começar";

    // Na boas-vindas, a janela não fecha (nem pelo X) antes do fim da contagem.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_remaining > 0) e.Cancel = true;
        base.OnClosing(e);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(AppInfo.GitHubUrl) { UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
