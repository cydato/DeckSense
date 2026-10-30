using System.Runtime.InteropServices;

namespace DeckSense.Service.Interop;

/// <summary>
/// Hängt sich an die Konsole des aufrufenden Prozesses an.
///
/// <para>
/// <b>Warum das nötig ist.</b> Der Dienst wird als <c>WinExe</c> gebaut, also
/// im Windows-Subsystem statt im Konsolensubsystem. Nur so ist sichergestellt,
/// dass <b>nie</b> ein Fenster erscheint - egal, wer ihn startet. Als
/// Konsolenprogramm hing das am Startweg: Über
/// <see cref="System.Diagnostics.Process"/> mit <c>CreateNoWindow</c> blieb es
/// unsichtbar, über die Aufgabenplanung konnte ein schwarzes Fenster
/// stehenbleiben. Genau das ist dem Nutzer nach einem Neustart passiert.
/// </para>
///
/// <para>
/// Der Preis: Ein Programm im Windows-Subsystem bekommt beim Start aus einer
/// Eingabeaufforderung <b>keine</b> Konsole zugeteilt, seine Ausgabe liefe also
/// ins Leere. Für die Diagnosemodi (<c>--once</c>, <c>--diag</c>,
/// <c>--sensors</c>, <c>--detect</c>, <c>--help</c>) wird sie deshalb hier
/// nachträglich besorgt.
/// </para>
///
/// <para>
/// Ohne aufrufende Konsole - etwa beim Start durch die Aufgabenplanung - /// schlägt das Anhängen fehl. Das ist der Normalfall und kein Fehler: Dann
/// bleibt die Ausgabe, wo sie hingehört, nämlich in der Protokolldatei.
/// </para>
///
/// <para>Siehe Entscheidung 0027.</para>
/// </summary>
internal static partial class ParentConsole
{
    /// <summary>Kennung für „die Konsole des Elternprozesses".</summary>
    private const uint ElternProzess = 0x0FFFFFFF;

    /// <summary>
    /// Versucht, die Konsole des Aufrufers zu übernehmen, und leitet die
    /// Standardausgabe dorthin um.
    /// </summary>
    /// <returns><c>true</c>, wenn eine Konsole da war.</returns>
    internal static bool Anhaengen()
    {
        try
        {
            if (!AttachConsole(ElternProzess))
            {
                return false;
            }

            // Nach dem Anhängen zeigen die zwischengespeicherten Streams noch
            // ins Leere - sie müssen neu auf die frisch übernommene Konsole
            // gesetzt werden.
            var ausgabe = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(ausgabe);
            Console.SetError(ausgabe);

            return true;
        }
        catch (Exception)
        {
            // Diagnoseausgabe ist Komfort. Scheitert sie, läuft der Dienst
            // trotzdem - und protokolliert in die Datei.
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);
}
