using System.Windows;
using Forms = System.Windows.Forms;

namespace BluetoothPlus.Services;

/// <summary>Ícone na bandeja do sistema (o WPF não tem um nativo; usa o do WinForms).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _scanItem;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Bluetooth+",
            Visible = true,
        };

        var startup = new Forms.ToolStripMenuItem("Iniciar com o Windows")
        {
            CheckOnClick = true,
            Checked = Installer.StartWithWindows,
        };
        startup.CheckedChanged += (_, _) => Installer.StartWithWindows = startup.Checked;

        _scanItem = new Forms.ToolStripMenuItem("Parar busca", null, (_, _) => ScanToggleRequested?.Invoke());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Bluetooth+", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(_scanItem);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add($"Feito por {AppInfo.Author} :)", null, (_, _) => CreditsRequested?.Invoke());
        menu.Items.Add("Sair", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public event Action? OpenRequested;
    public event Action? ScanToggleRequested;
    public event Action? ExitRequested;
    public event Action? CreditsRequested;

    public void SetScanning(bool scanning) => _scanItem.Text = scanning ? "Parar busca" : "Iniciar busca";

    /// <summary>
    /// Mostra os aparelhos conectados (e a bateria) na dica do ícone e no topo do menu.
    /// </summary>
    public void SetConnectedDevices(IReadOnlyList<string> lines)
    {
        // A dica do NotifyIcon aceita no máximo 127 caracteres.
        var tooltip = lines.Count == 0 ? "Bluetooth+\nNenhum dispositivo conectado" : "Bluetooth+\n" + string.Join("\n", lines);
        _icon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";

        var menu = _icon.ContextMenuStrip!;
        foreach (var old in menu.Items.OfType<Forms.ToolStripItem>().Where(i => Equals(i.Tag, StatusTag)).ToList())
            menu.Items.Remove(old);

        var items = lines.Count == 0 ? new[] { "Nenhum dispositivo conectado" } : lines;
        int index = 0;
        foreach (var line in items)
            menu.Items.Insert(index++, new Forms.ToolStripMenuItem(line) { Enabled = false, Tag = StatusTag });
        menu.Items.Insert(index, new Forms.ToolStripSeparator { Tag = StatusTag });
    }

    private const string StatusTag = "status";

    public void Notify(string title, string text) => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
