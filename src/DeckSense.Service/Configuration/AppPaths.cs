namespace DeckSense.Service.Configuration;

/// <summary>
/// Bestimmt, wo Konfiguration und Protokoll liegen.
///
/// Zwei Fälle, die sich unterscheiden müssen:
///
/// * **Tragbar** - das Programm liegt in einem Ordner, in den geschrieben
///   werden darf, etwa einem entpackten Archiv. Dann liegt alles daneben, und
///   man kann den Ordner mitnehmen.
/// * **Installiert** - das Programm liegt unter `Program Files`. Dort gehören
///   keine Nutzerdaten hin, auch wenn der Dienst erhöht läuft und es technisch
///   könnte. Konfiguration und Protokoll wandern dann nach
///   `%ProgramData%\Cydato\DeckSense`.
///
/// **Entschieden wird am Pfad, nicht an einem Schreibversuch.** Ein
/// Schreibversuch wäre verlockend, aber falsch: Der Dienst läuft über die
/// Aufgabenplanung erhöht und dürfte dann auch unter `Program Files`
/// schreiben - dieselbe Installation landete also mal hier, mal dort, je
/// nachdem wie sie gestartet wurde. Der Pfad ist eindeutig.
///
/// Der Schreibversuch bleibt nur als zweite Stufe für den tragbaren Fall: Liegt
/// das Programm außerhalb von `Program Files` in einem schreibgeschützten
/// Ordner, wird ebenfalls ausgewichen.
/// </summary>
internal static class AppPaths
{
    private const string Hersteller = "Cydato";
    private const string Produkt = "DeckSense";

    private static string? _verzeichnis;

    /// <summary>Wurde auf den Ausweichort zurückgegriffen?</summary>
    internal static bool NutztProgramData { get; private set; }

    /// <summary>
    /// Warum ausgewichen wurde, für das Protokoll. Die beiden Fälle sind nicht
    /// dasselbe, und „nicht beschreibbar" wäre für eine Installation unter
    /// „Program Files" schlicht falsch: Dort <b>darf</b> der erhöht laufende
    /// Dienst schreiben, er soll nur nicht.
    /// </summary>
    internal static string Grund { get; private set; } = string.Empty;

    internal static string DatenVerzeichnis()
    {
        if (_verzeichnis is not null)
        {
            return _verzeichnis;
        }

        string neben = AppContext.BaseDirectory;
        bool unterProgramFiles = LiegtUnterProgramFiles(neben);

        if (!unterProgramFiles && IstBeschreibbar(neben))
        {
            NutztProgramData = false;
            _verzeichnis = neben;
            return _verzeichnis;
        }

        Grund = unterProgramFiles
            ? "Das Programm liegt unter „Program Files\"; dort gehören keine Nutzerdaten hin"
            : "Der Programmordner ist nicht beschreibbar";

        string programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Hersteller,
            Produkt);

        try
        {
            Directory.CreateDirectory(programData);
            NutztProgramData = true;
            _verzeichnis = programData;
        }
        catch (Exception)
        {
            // Auch das ging nicht. Dann bleibt nur der Programmordner - der
            // Dienst läuft trotzdem, nur ohne gespeicherte Konfiguration.
            NutztProgramData = false;
            _verzeichnis = neben;
        }

        return _verzeichnis;
    }

    private static bool LiegtUnterProgramFiles(string verzeichnis)
    {
        string[] geschuetzt =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        try
        {
            string voll = Path.GetFullPath(verzeichnis).TrimEnd(Path.DirectorySeparatorChar);

            foreach (string ordner in geschuetzt)
            {
                if (ordner.Length == 0)
                {
                    continue;
                }

                string basis = Path.GetFullPath(ordner).TrimEnd(Path.DirectorySeparatorChar);

                if (voll.StartsWith(basis + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Im Zweifel nicht ausweichen.
        }

        return false;
    }

    private static bool IstBeschreibbar(string verzeichnis)
    {
        string probe = Path.Combine(verzeichnis, $".schreibprobe-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
