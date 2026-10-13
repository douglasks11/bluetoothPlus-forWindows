using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BluetoothPlus.Services;
using BluetoothPlus.Views;

namespace BluetoothPlus;

public partial class App : Application
{
    private const string MutexName = @"Local\BluetoothPlus.Instance";
    private const string ShowEventName = @"Local\BluetoothPlus.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        ThemeManager.Initialize();

        if (e.Args.Contains("--uninstall"))
        {
            RunUninstall();
            return;
        }

        // Instância única: uma segunda execução só traz a janela existente para frente.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        if (!Installer.IsRunningInstalled && !Installer.SkipInstallPrompt && OfferInstall())
            return;

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--tray"))
        {
            window.Show();
            if (!SettingsStore.WelcomeShown)
            {
                // Depois de Show(): a boas-vindas abre centralizada sobre a janela principal.
                window.Dispatcher.BeginInvoke(() =>
                {
                    AboutWindow.ShowWelcome(window);
                    SettingsStore.WelcomeShown = true;
                }, DispatcherPriority.ApplicationIdle);
            }
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "ShowListener" }.Start();
    }

    /// <summary>Retorna true se instalou e iniciou a cópia instalada (este processo deve sair).</summary>
    private bool OfferInstall()
    {
        var answer = MessageBox.Show(
            "Instalar o Bluetooth+ neste computador?\n\n" +
            "• Atalho no menu Iniciar\n" +
            "• Desinstalação pelas Configurações do Windows\n" +
            "• Não precisa de permissão de administrador\n\n" +
            "Escolha \"Não\" para usar sem instalar (não perguntaremos de novo).",
            "Bluetooth+", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            Installer.SkipInstallPrompt = true;
            return false;
        }

        try
        {
            Installer.Install();
        }
        catch (IOException)
        {
            MessageBox.Show("Não foi possível instalar: o Bluetooth+ instalado está aberto.\n" +
                            "Feche-o pelo ícone na bandeja (Sair) e tente de novo.",
                "Bluetooth+", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Libera a instância única antes de iniciar a cópia instalada.
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
        Shutdown();
        return true;
    }

    private void RunUninstall()
    {
        var answer = MessageBox.Show("Desinstalar o Bluetooth+?", "Bluetooth+",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            Installer.Uninstall();
            MessageBox.Show("Bluetooth+ foi removido.", "Bluetooth+", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Shutdown();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Bluetooth+ — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
