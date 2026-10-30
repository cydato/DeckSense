namespace DeckSense.Setup;

/// <summary>
/// Kopiert das Programm in einen Zielordner, etwa
/// <c>C:\Program Files\Cydato\DeckSense</c>.
///
/// Warum überhaupt: Eine geplante Aufgabe, die eine Exe von einem Netzlaufwerk
/// startet, ist fragil - beim Anmelden ist die Freigabe womöglich noch nicht
/// verbunden. Das Programm gehört auf eine lokale Platte.
///
/// Das ist kein Installationsprogramm, sondern ein Kopiervorgang mit zwei
/// Regeln. Mehr braucht es nicht, und weniger wäre nicht nachvollziehbar.
///
/// <para>
/// <b>Beim Aktualisieren wird der laufende Dienst beendet und danach wieder
/// gestartet.</b> Anders geht es nicht: Ein laufender Prozess hält seine eigene
/// Exe gesperrt, und ein Kopierversuch darüber scheitert. Weil dieser Vorgang
/// ohnehin erhöht läuft, kann er den erhöht laufenden Dienst auch beenden - von
/// der Oberfläche aus allein wäre das nicht möglich.
/// </para>
/// </summary>
internal static class Installer
{
    internal const string Vorgabeziel = @"C:\Program Files\Cydato\DeckSense";

    /// <summary>
    /// Diese Namen werden nie überschrieben oder mitkopiert.
    ///
    /// <c>appsettings.json</c> enthält die Einstellungen des Nutzers samt
    /// Laufwerksliste - eine Aktualisierung darf sie nicht wegwerfen.
    /// <c>logs</c> ist Laufzeitkram.
    /// </summary>
    private static readonly string[] NichtUeberschreiben = ["appsettings.json"];

    private static readonly string[] NichtKopieren = ["logs"];

    internal sealed record Ergebnis(bool Erfolg, int Dateien, string Meldung);

    internal static Ergebnis Kopieren(string quelle, string ziel)
    {
        try
        {
            if (!Directory.Exists(quelle))
            {
                return new Ergebnis(false, 0, $"Der Quellordner fehlt: {quelle}");
            }

            string quelleVoll = Path.GetFullPath(quelle);
            string zielVoll = Path.GetFullPath(ziel);

            if (string.Equals(quelleVoll, zielVoll, StringComparison.OrdinalIgnoreCase))
            {
                return new Ergebnis(false, 0, "Quelle und Ziel sind derselbe Ordner.");
            }

            // Der laufende Dienst hält seine eigene Exe gesperrt. Also erst
            // beenden - und hinterher wieder starten, wenn er vorher lief.
            //
            // Gemeint ist ausschließlich ein Dienst **aus dem Zielordner**.
            // Über den Prozessnamen allein zu gehen wäre falsch: Eine
            // Installation in einen anderen Ordner würde dann einen Dienst
            // mitreißen, der dort gar nicht liegt.
            string zielExe = Path.Combine(zielVoll, "DeckSense.Service.exe");
            bool liefVorher = ServiceProbe.LaeuftAus(zielExe);

            if (liefVorher && !ServiceProbe.Beenden(zielExe))
            {
                return new Ergebnis(false, 0,
                    "Der laufende Dienst ließ sich nicht beenden. Ohne das kann seine Exe nicht "
                    + "überschrieben werden.");
            }

            Directory.CreateDirectory(zielVoll);

            int anzahl = 0;
            int uebersprungen = 0;

            foreach (string datei in Directory.EnumerateFiles(quelleVoll, "*", SearchOption.AllDirectories))
            {
                string relativ = Path.GetRelativePath(quelleVoll, datei);
                string ersterTeil = relativ.Split(Path.DirectorySeparatorChar)[0];

                if (NichtKopieren.Contains(ersterTeil, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Schreibproben von AppPaths haben im Ziel nichts verloren.
                if (Path.GetFileName(relativ).StartsWith(".schreibprobe-", StringComparison.Ordinal))
                {
                    continue;
                }

                string zielDatei = Path.Combine(zielVoll, relativ);

                if (NichtUeberschreiben.Contains(Path.GetFileName(relativ), StringComparer.OrdinalIgnoreCase)
                    && File.Exists(zielDatei))
                {
                    uebersprungen++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(zielDatei)!);
                File.Copy(datei, zielDatei, overwrite: true);
                anzahl++;
            }

            string hinweis = uebersprungen > 0
                ? $" Vorhandene Konfiguration behalten ({uebersprungen} Datei übersprungen)."
                : string.Empty;

            string dienst = string.Empty;

            if (liefVorher)
            {
                try
                {
                    ServiceProbe.Starten(zielExe);
                    dienst = " Dienst beendet und aus dem Zielordner neu gestartet.";
                }
                catch (Exception fehler)
                {
                    // Kopiert ist kopiert. Dass der Dienst nicht wieder
                    // hochkam, darf den Vorgang nicht als gescheitert melden - // aber gesagt werden muss es.
                    dienst = $" Achtung: Der Dienst wurde beendet, ließ sich aber nicht neu starten "
                             + $"({fehler.Message}).";
                }
            }

            return new Ergebnis(true, anzahl, $"{anzahl} Dateien kopiert.{hinweis}{dienst}");
        }
        catch (UnauthorizedAccessException)
        {
            return new Ergebnis(false, 0, "No permission for the target folder.");
        }
        catch (Exception fehler)
        {
            return new Ergebnis(false, 0, fehler.Message);
        }
    }

    /// <summary>Ist in diesem Zielordner bereits eine Fassung installiert?</summary>
    internal static bool IstInstalliert(string ziel)
    {
        try
        {
            return File.Exists(Path.Combine(ziel, "DeckSense.Service.exe"));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
