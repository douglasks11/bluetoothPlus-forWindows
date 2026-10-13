using System.Windows;
using Microsoft.Win32;

namespace BluetoothPlus.Services;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// Troca entre os temas claro e escuro substituindo o dicionário de cores
/// (Themes/*.xaml). Os elementos usam DynamicResource, então mudam na hora.
/// </summary>
public static class ThemeManager
{
    private const string SettingsKey = @"Software\BluetoothPlus";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _current;

    /// <summary>Disparado depois que as cores mudam (escolha do usuário ou tema do Windows).</summary>
    public static event Action? Changed;

    public static bool IsLight { get; private set; }

    public static ThemeMode Mode
    {
        get => Registry.CurrentUser.OpenSubKey(SettingsKey)?.GetValue("Theme") is string s
               && Enum.TryParse<ThemeMode>(s, out var mode) ? mode : ThemeMode.System;
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue("Theme", value.ToString());
            Apply();
        }
    }

    public static void Initialize()
    {
        Apply();
        // Acompanha a troca de tema do Windows quando o modo é "Sistema".
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static void Apply()
    {
        IsLight = Mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => WindowsUsesLightTheme(),
        };

        var source = new Uri($"pack://application:,,,/Themes/{(IsLight ? "Light" : "Dark")}.xaml");
        if (_current?.Source == source) return;

        // Remove o tema anterior — inclusive o declarado no App.xaml — porque, entre
        // dicionários mesclados, o último adicionado vence.
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var old in dictionaries.Where(d => d.Source?.OriginalString.Contains("Themes/") == true).ToList())
            dictionaries.Remove(old);

        _current = new ResourceDictionary { Source = source };
        dictionaries.Add(_current);
        Changed?.Invoke();
    }

    private static bool WindowsUsesLightTheme()
        => Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme") is not 0;
}
