using System.Text.Json;
using System.Text.Json.Serialization;
using DeckSense.Service.Logging;

namespace DeckSense.Service.Configuration;

public sealed class HttpConfig
{
    /// <summary>
    /// Reserviert für die HTTP-API ab Version 0.6.0. 8085 wäre falsch, das
    /// belegt der Webserver von LibreHardwareMonitor.
    /// </summary>
    public int Port { get; init; } = 5891;
}

public sealed class IntervalConfig
{
    public int FastMs { get; init; } = 1000;

    /// <summary>Reserviert für die Laufwerkstemperaturen ab Version 0.5.0.</summary>
    public int StorageMs { get; init; } = 15000;
}

/// <summary>
/// Welche Hardwaregruppen LibreHardwareMonitor überhaupt anlegen darf.
/// Standard ist jeweils das Sichere: nur GPU.
/// </summary>
public sealed class HardwareConfig
{
    /// <summary>
    /// CPU-Gruppe. <b>Standard aus.</b> Sie wird allein für <c>cpu.temp</c>
    /// gebraucht, und das setzt den Kernel-Treiber PawnIO voraus - siehe
    /// Entscheidung 0012. Wer den Treiber installiert hat, setzt das hier auf
    /// <c>true</c>.
    /// </summary>
    public bool Cpu { get; init; }

    public bool Gpu { get; init; } = true;

    /// <summary>
    /// Storage-Gruppe. <b>Standard aus</b> und bis Version 0.5.0 ohne Wirkung.
    /// Nicht einschalten, bevor geklärt ist, ob das Aufzählen die
    /// ausgeschlossene Intel-NVMe anspricht.
    /// </summary>
    public bool Storage { get; init; }
}

/// <summary>
/// Ein Laufwerk in der Freigabe- oder Ausschlussliste. Erkannt wird über die
/// Seriennummer, nicht über den Anzeigenamen oder die Laufwerksnummer: Nummern
/// verschieben sich nach einem Portwechsel, Seriennummern nicht.
/// </summary>
public sealed class StorageDeviceConfig
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Serial { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;
}

public sealed class StorageConfig
{
    /// <summary>Nur diese Laufwerke werden überhaupt angefasst.</summary>
    public List<StorageDeviceConfig> Allowed { get; init; } = [];

    /// <summary>
    /// Harte Sperre. Ein Laufwerk auf dieser Liste wird nie abgefragt, auch
    /// wenn es gleichzeitig in <see cref="Allowed"/> steht. Damit ein
    /// ausgeschlossenes Laufwerk je gelesen wird, muss es hier bewusst entfernt
    /// <b>und</b> dort eingetragen werden - zwei getrennte Schritte für eine
    /// Entscheidung, die schon einmal Ärger gemacht hat.
    /// </summary>
    public List<StorageDeviceConfig> Excluded { get; init; } = [];
}

public sealed class LoggingConfig
{
    public string Path { get; init; } = "logs/decksense.log";

    public LogLevel Level { get; init; } = LogLevel.Information;

    public int MaxFileSizeKb { get; init; } = 1024;

    public int MaxFiles { get; init; } = 3;
}

public sealed class AppConfig
{
    public const string FileName = "appsettings.json";

    private const int MinimumFastMs = 250;
    private const int MinimumStorageMs = 10_000;

    public HttpConfig Http { get; init; } = new();

    public IntervalConfig Intervals { get; init; } = new();

    public HardwareConfig Hardware { get; init; } = new();

    public StorageConfig Storage { get; init; } = new();

    public LoggingConfig Logging { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lädt die Konfiguration neben der Exe. Fehlt die Datei oder ist sie
    /// kaputt, laufen die Vorgaben - der Dienst startet trotzdem. Hinweise
    /// dazu landen in <paramref name="notes"/>, weil das Protokoll zu diesem
    /// Zeitpunkt noch nicht steht.
    /// </summary>
/// <param name="darfErzeugen">
    /// <c>false</c> für die rein lesenden Diagnosemodi. Diese sollen nichts
    /// hinterlassen - <c>--detect</c> sagt in der Hilfe ausdrücklich "ändert
    /// nichts" und hat trotzdem eine Konfiguration samt Seriennummern angelegt.
    /// Siehe Entscheidung 0027.
    /// </param>
    public static AppConfig Load(string directory, out List<string> notes, bool darfErzeugen = true)
    {
        notes = [];

        // Beim allerersten Start gibt es noch keine Konfiguration. Dann wird sie
        // aus den tatsächlich vorhandenen Laufwerken erzeugt, damit DeckSense
        // auf fremder Hardware ohne Handarbeit läuft.
        if (darfErzeugen)
        {
            ConfigBootstrap.EnsureExists(directory, notes);
        }

        string path = Path.Combine(directory, FileName);
        AppConfig config;

        if (!File.Exists(path))
        {
            notes.Add($"{FileName} nicht gefunden unter {path} - es gelten die Vorgaben.");
            config = new AppConfig();
        }
        else
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options)
                         ?? new AppConfig();
            }
            catch (Exception error)
            {
                notes.Add($"{FileName} ist nicht lesbar ({error.GetType().Name}: {error.Message}) - es gelten die Vorgaben.");
                config = new AppConfig();
            }
        }

        return config.Clamped(notes);
    }

    /// <summary>
    /// Zieht die Intervalle in den erlaubten Bereich. Die Untergrenze für
    /// Laufwerke ist eine Projektvorgabe: häufigere SMART-Abfragen sind genau
    /// das Risiko, das dieses Werkzeug vermeiden soll.
    /// </summary>
    private AppConfig Clamped(List<string> notes)
    {
        int fast = Intervals.FastMs;
        int storage = Intervals.StorageMs;

        if (fast < MinimumFastMs)
        {
            notes.Add($"intervals.fastMs war {fast} ms und wurde auf {MinimumFastMs} ms angehoben.");
            fast = MinimumFastMs;
        }

        if (storage < MinimumStorageMs)
        {
            notes.Add($"intervals.storageMs war {storage} ms und wurde auf {MinimumStorageMs} ms angehoben.");
            storage = MinimumStorageMs;
        }

        if (fast == Intervals.FastMs && storage == Intervals.StorageMs)
        {
            return this;
        }

        return new AppConfig
        {
            Http = Http,
            Hardware = Hardware,
            Storage = Storage,
            Logging = Logging,
            Intervals = new IntervalConfig { FastMs = fast, StorageMs = storage }
        };
    }
}
