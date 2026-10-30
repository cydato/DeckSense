using Microsoft.Win32;

namespace DeckSense.Service.Interop;

/// <summary>
/// Stellt fest, ob der Kernel-Treiber PawnIO installiert ist.
///
/// LibreHardwareMonitor braucht ihn für MSR-Zugriffe und damit für die
/// CPU-Temperatur. Fehlt er, liefert die Bibliothek <b>keine Ausnahme</b>,
/// sondern stillschweigend Nullen - siehe Entscheidung 0007. Deshalb wird die
/// Anwesenheit hier aktiv geprüft, statt sich auf Fehler zu verlassen.
///
/// Geprüft wird derselbe Registry-Wert, den auch LibreHardwareMonitor liest.
/// Dass der Schlüssel existiert, heißt nicht, dass der Treiber läuft - es ist
/// nur die erste, billige Aussage.
/// </summary>
internal static class PawnIoPresence
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    internal static string? InstalledVersion { get; } = ReadVersion();

    internal static bool IsInstalled => InstalledVersion is not null;

    private static string? ReadVersion()
    {
        try
        {
            using RegistryKey baseKey =
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
