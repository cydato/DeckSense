using System.Reflection;

namespace DeckSense.Service;

/// <summary>
/// Name und Version an einer Stelle, gelesen aus dem Assembly. Die Version
/// steht damit nur im csproj und kann nicht zwischen Code, Hilfe und Protokoll
/// auseinanderlaufen.
/// </summary>
internal static class AppInfo
{
    public const string Name = "DeckSense Sensordienst";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Der Wert trägt bei Buildvarianten ein "+<commit>" hinten dran.
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
