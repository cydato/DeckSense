namespace DeckSense.Setup;

internal static class Program
{
    /// <summary>Schalter für den Lauf mit erhöhten Rechten.</summary>
    internal const string SchalterEinrichten = "--aufgabe-einrichten";

    internal const string SchalterEntfernen = "--aufgabe-entfernen";

    internal const string SchalterInstallieren = "--installieren";

    /// <summary>
    /// Den Dienst beenden. Braucht erhöhte Rechte, weil der Dienst selbst
    /// erhöht läuft - siehe <see cref="ServiceProbe.Beenden"/>.
    /// </summary>
    internal const string SchalterDienstBeenden = "--dienst-beenden";


    [STAThread]
    private static int Main(string[] args)
    {
        // Die beiden Schalter sind der Weg, auf dem sich das Programm selbst
        // mit erhöhten Rechten aufruft - nur für die geplante Aufgabe, nicht
        // für das Fenster. Siehe Entscheidung 0020.
        if (args.Length >= 1 && args[0] == SchalterEinrichten)
        {
            if (args.Length < 2)
            {
                return 2;
            }

            (bool erfolg, _) = TaskSetup.Einrichten(args[1]);
            return erfolg ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == SchalterEntfernen)
        {
            (bool erfolg, _) = TaskSetup.Entfernen();
            return erfolg ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == SchalterInstallieren)
        {
            if (args.Length < 3)
            {
                return 2;
            }

            return Installer.Kopieren(args[1], args[2]).Erfolg ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == SchalterDienstBeenden)
        {
            return ServiceProbe.Beenden() ? 0 : 1;
        }


        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
