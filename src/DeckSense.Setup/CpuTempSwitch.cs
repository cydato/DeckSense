using System.Text.RegularExpressions;
using DeckSense.Service.Interop;

namespace DeckSense.Setup;

/// <summary>
/// Schaltet <c>hardware.cpu</c> in der <c>appsettings.json</c> um - den
/// Schalter, der die CPU-Temperatur freigibt.
///
/// <para>
/// <b>Warum das ein eigener Knopf mit Warnung ist.</b> Die CPU-Temperatur ist
/// der einzige Wert in DeckSense, der einen <b>Kernel-Treiber</b> voraussetzt:
/// PawnIO, den LibreHardwareMonitor für die MSR-Zugriffe braucht. Auf einem
/// Rechner mit Onlinespielen ist das keine Nebensächlichkeit, sondern die
/// Entscheidung, die
/// <see href="https://github.com/cydato/DeckSense/blob/main/docs/entscheidungen.md">
/// Entscheidung 0012</see> ursprünglich verneint hat. Der Nutzer darf sie
/// treffen - aber mit den Fakten vor Augen, nicht beiläufig in einer
/// Konfigurationsdatei.
/// </para>
///
/// <para>
/// <b>Geschrieben wird mit einem gezielten Textersatz, nicht über einen
/// JSON-Serialisierer.</b> Die Datei enthält Kommentare zu jedem Schlüssel;
/// einmal durch <c>JsonSerializer</c> gedreht, wären sie weg. Ersetzt wird
/// deshalb nur das eine Vorkommen von <c>"cpu": true|false</c>.
/// </para>
/// </summary>
internal static class CpuTempSwitch
{
    private const string Hersteller = "Cydato";
    private const string Produkt = "DeckSense";

    /// <summary>Findet <c>hardware.cpu</c> samt Wert, ohne den Rest anzufassen.</summary>
    private static readonly Regex Schalter = new(
        "(\"cpu\"\\s*:\\s*)(true|false)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool PawnIoInstalliert => PawnIoPresence.IsInstalled;

    internal static string? PawnIoVersion => PawnIoPresence.InstalledVersion;

    /// <summary>
    /// Wo die Konfiguration liegt. Dieselbe Regel wie im Dienst: neben der Exe,
    /// außer sie liegt unter „Program Files" - dann unter %ProgramData%.
    /// </summary>
    internal static string? Pfad(string? servicePfad)
    {
        if (servicePfad is null)
        {
            return null;
        }

        string? ordner = Path.GetDirectoryName(servicePfad);

        if (ordner is null)
        {
            return null;
        }

        string neben = Path.Combine(ordner, "appsettings.json");

        if (File.Exists(neben))
        {
            return neben;
        }

        string programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Hersteller,
            Produkt,
            "appsettings.json");

        return File.Exists(programData) ? programData : null;
    }

    /// <summary>
    /// Der aktuelle Stand, oder <c>null</c>, wenn die Datei fehlt oder den
    /// Schlüssel nicht enthält.
    /// </summary>
    internal static bool? Lesen(string? pfad)
    {
        if (pfad is null || !File.Exists(pfad))
        {
            return null;
        }

        try
        {
            Match treffer = Schalter.Match(File.ReadAllText(pfad));

            return treffer.Success
                ? string.Equals(treffer.Groups[2].Value, "true", StringComparison.OrdinalIgnoreCase)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static (bool Erfolg, string Meldung) Setzen(string? pfad, bool an)
    {
        if (pfad is null || !File.Exists(pfad))
        {
            return (false, "appsettings.json was not found. Has the service ever run?");
        }

        try
        {
            string text = File.ReadAllText(pfad);

            if (!Schalter.IsMatch(text))
            {
                return (false, "appsettings.json has no \"cpu\" key under \"hardware\".");
            }

            string neu = Schalter.Replace(text, m => m.Groups[1].Value + (an ? "true" : "false"), 1);

            if (neu == text)
            {
                return (true, an ? "Was already true." : "Was already false.");
            }

            File.WriteAllText(pfad, neu);

            return (true, an
                ? "CPU temperature turned on. The service picks that up on its next start."
                : "CPU temperature turned off. The service picks that up on its next start.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"No permission to write {pfad}.");
        }
        catch (Exception fehler)
        {
            return (false, fehler.Message);
        }
    }
}
