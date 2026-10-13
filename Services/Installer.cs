using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace BluetoothPlus.Services;

/// <summary>
/// Instalação por usuário (sem admin): copia o .exe para
/// %LOCALAPPDATA%\Programs\BluetoothPlus, cria atalho no menu Iniciar e
/// registra em "Aplicativos instalados" para desinstalar pelo Windows.
/// </summary>
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BluetoothPlus";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\BluetoothPlus";
    private const string RunValue = "BluetoothPlus";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BluetoothPlus");

    public static string InstalledExe => Path.Combine(InstallDir, "BluetoothPlus.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Bluetooth+.lnk");

    private static string CurrentExe => Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido.");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Usuário escolheu usar o app sem instalar — não perguntar de novo.</summary>
    public static bool SkipInstallPrompt
    {
        get => Registry.CurrentUser.OpenSubKey(SettingsKey)?.GetValue("SkipInstallPrompt") is 1;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("SkipInstallPrompt", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    public static bool StartWithWindows
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) is string;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{CurrentExe}\" --tray");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public static void Install()
    {
        Directory.CreateDirectory(InstallDir);
        File.Copy(CurrentExe, InstalledExe, overwrite: true);
        CreateShortcut(ShortcutPath, InstalledExe);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Bluetooth+");
        key.SetValue("DisplayVersion", version);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    public static void Uninstall()
    {
        StartWithWindows = false;
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false);
        if (Directory.Exists(SettingsStore.Folder)) Directory.Delete(SettingsStore.Folder, recursive: true);

        // Um .exe em execução não pode apagar a si mesmo: o cmd apaga a pasta
        // depois que este processo terminar.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    private static void CreateShortcut(string shortcutPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "Gerenciador de Bluetooth";
        shortcut.Save();
    }
}
