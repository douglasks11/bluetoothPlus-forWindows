using System.Reflection;

namespace BluetoothPlus;

/// <summary>Autoria e informações exibidas nos créditos e na tela de boas-vindas.</summary>
public static class AppInfo
{
    public const string Author = "Douglas Oliveira";
    public const string GitHubUrl = "https://github.com/douglasks11";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
