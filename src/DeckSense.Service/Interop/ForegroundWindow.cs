using System.Runtime.InteropServices;

namespace DeckSense.Service.Interop;

/// <summary>
/// Ermittelt die Prozess-ID des Fensters im Vordergrund. Nötig, um im
/// RTSS-Shared-Memory den Eintrag der Anwendung zu finden, die gerade
/// tatsächlich im Vordergrund läuft.
///
/// Das funktioniert nur in einer Benutzersitzung. In Session 0 - also als
/// klassischer Windows-Dienst - gibt es kein Vordergrundfenster. Genau deshalb
/// startet DeckSense über die Aufgabenplanung in der Benutzersitzung.
/// </summary>
internal static partial class ForegroundWindow
{
    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>
    /// Prozess-ID des Vordergrundfensters, oder 0, wenn keines zu ermitteln
    /// ist (etwa während eines Sitzungswechsels oder bei einem
    /// Sperrbildschirm).
    /// </summary>
    internal static uint GetProcessId()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        return GetWindowThreadProcessId(window, out uint processId) == 0 ? 0 : processId;
    }
}
