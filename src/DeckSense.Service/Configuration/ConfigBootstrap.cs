using System.Text;
using DeckSense.Service.Interop;

namespace DeckSense.Service.Configuration;

/// <summary>
/// Erzeugt beim ersten Start eine <c>appsettings.json</c> aus den tatsächlich
/// vorhandenen Laufwerken.
///
/// Damit läuft DeckSense auf fremder Hardware ohne Handarbeit, und im
/// Quellcode-Archiv muss keine Konfiguration mit fremden Seriennummern liegen.
/// Eine vorhandene Datei wird <b>nie</b> überschrieben - wer sie angepasst hat,
/// behält seine Anpassung.
/// </summary>
internal static class ConfigBootstrap
{
    /// <summary>Längster Teil des Modellnamens, der in eine Sensor-ID wandert.</summary>
    private const int MaxSlugLength = 24;

    internal static void EnsureExists(string directory, List<string> notes)
    {
        string path = Path.Combine(directory, AppConfig.FileName);

        if (File.Exists(path))
        {
            return;
        }

        IReadOnlyList<StorageDeviceInfo> drives;
        try
        {
            drives = StorageDetector.Enumerate();
        }
        catch (Exception error)
        {
            notes.Add($"Laufwerke konnten nicht erkannt werden ({error.GetType().Name}: {error.Message}). "
                      + "Ohne Admin-Rechte ist das zu erwarten. Die erzeugte Konfiguration bleibt ohne Laufwerke.");
            drives = [];
        }

        try
        {
            File.WriteAllText(path, Build(drives), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            int allowed = drives.Count(d => d.LooksLikeFixedDisk);
            notes.Add($"{AppConfig.FileName} war nicht vorhanden und wurde neu erzeugt: "
                      + $"{allowed} von {drives.Count} erkannten Laufwerken freigegeben.");

            foreach (StorageDeviceInfo skipped in drives.Where(d => !d.LooksLikeFixedDisk))
            {
                notes.Add($"Nicht freigegeben, weil Wechselmedium oder ohne Größe: "
                          + $"'{skipped.FriendlyName}' ({skipped.BusType}). Bei Bedarf von Hand eintragen.");
            }
        }
        catch (Exception error)
        {
            notes.Add($"{AppConfig.FileName} konnte nicht geschrieben werden "
                      + $"({error.GetType().Name}: {error.Message}). Es gelten die Vorgaben, "
                      + "und es werden keine Laufwerkstemperaturen gelesen.");
        }
    }

    private static string Build(IReadOnlyList<StorageDeviceInfo> drives)
    {
        var builder = new StringBuilder();

        builder.AppendLine("{");
        builder.AppendLine("  // Von DeckSense beim ersten Start erzeugt. Kommentare sind erlaubt,");
        builder.AppendLine("  // der Loader überliest sie. Diese Datei wird nie überschrieben.");
        builder.AppendLine();
        builder.AppendLine("  \"http\": {");
        builder.AppendLine("    \"port\": 5891");
        builder.AppendLine("  },");
        builder.AppendLine();
        builder.AppendLine("  \"intervals\": {");
        builder.AppendLine("    \"fastMs\": 1000,");
        builder.AppendLine("    // Laufwerke. Minimum 10000, wird beim Start erzwungen.");
        builder.AppendLine("    \"storageMs\": 15000");
        builder.AppendLine("  },");
        builder.AppendLine();
        builder.AppendLine("  \"hardware\": {");
        builder.AppendLine("    // Braucht den Kernel-Treiber PawnIO, nur für cpu.temp.");
        builder.AppendLine("    \"cpu\": false,");
        builder.AppendLine("    \"gpu\": true,");
        builder.AppendLine("    // Die Laufwerkstemperaturen kommen nicht hierüber, sondern über die");
        builder.AppendLine("    // Storage-WMI weiter unten.");
        builder.AppendLine("    \"storage\": false");
        builder.AppendLine("  },");
        builder.AppendLine();
        builder.AppendLine("  \"storage\": {");
        builder.AppendLine("    // Nur diese Laufwerke werden angefasst. Zugeordnet wird über die");
        builder.AppendLine("    // Seriennummer, nicht über die Laufwerksnummer - Nummern verschieben sich.");
        builder.AppendLine("    // Die Id steht in der API und in den Tastenzuordnungen: Wer sie ändert,");
        builder.AppendLine("    // muss die betroffenen Tasten neu zuweisen.");
        builder.Append("    \"allowed\": [");

        StorageDeviceInfo[] allowed = [.. drives.Where(d => d.LooksLikeFixedDisk)];

        for (int index = 0; index < allowed.Length; index++)
        {
            StorageDeviceInfo drive = allowed[index];
            builder.AppendLine();
            builder.Append(FormattableString.Invariant(
                $"      {{ \"id\": \"{BuildId(drive)}\", \"name\": \"{Escape(drive.FriendlyName)}\", \"serial\": \"{Escape(drive.Serial)}\" }}"));

            if (index < allowed.Length - 1)
            {
                builder.Append(',');
            }

            builder.Append(FormattableString.Invariant(
                $"  // {drive.MediaType}, {drive.BusType}, {drive.SizeGigabytes:0} GB, Laufwerk {drive.DeviceNumber}"));
        }

        if (allowed.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("    ],");
        }
        else
        {
            builder.AppendLine("],");
        }

        builder.AppendLine();
        builder.AppendLine("    // Harte Sperre mit Vorrang: Ein Laufwerk hier wird nie abgefragt, auch");
        builder.AppendLine("    // wenn es gleichzeitig oben steht. Für Laufwerke, die in Ruhe bleiben sollen.");
        builder.Append("    \"excluded\": [");

        StorageDeviceInfo[] skipped = [.. drives.Where(d => !d.LooksLikeFixedDisk)];
        if (skipped.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("      // Nicht freigegeben, weil Wechselmedium oder ohne Größe. Sie stehen");
            builder.AppendLine("      // hier nur als Hinweis, nicht als Sperre:");

            foreach (StorageDeviceInfo drive in skipped)
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"      // {drive.FriendlyName} - {drive.BusType}, Seriennummer {drive.Serial}"));
            }

            builder.AppendLine("    ]");
        }
        else
        {
            builder.AppendLine("]");
        }

        builder.AppendLine("  },");
        builder.AppendLine();
        builder.AppendLine("  \"logging\": {");
        builder.AppendLine("    \"path\": \"logs/decksense.log\",");
        builder.AppendLine("    // Debug zeigt zusätzlich die Rohwerte von RTSS.");
        builder.AppendLine("    \"level\": \"Information\",");
        builder.AppendLine("    \"maxFileSizeKb\": 1024,");
        builder.AppendLine("    \"maxFiles\": 3");
        builder.AppendLine("  }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>
    /// Baut eine stabile, lesbare Sensor-Id aus Modellname und den letzten vier
    /// Zeichen der Seriennummer. Die Seriennummer muss mit hinein: Zwei
    /// baugleiche Laufwerke hätten sonst dieselbe Id. Der Zusatz steht deshalb
    /// immer dort, nicht nur im Konfliktfall - sonst würde das Hinzufügen eines
    /// zweiten gleichen Laufwerks die Id des ersten verändern.
    /// </summary>
    internal static string BuildId(StorageDeviceInfo drive)
    {
        string slug = Slug(drive.FriendlyName);
        string suffix = SerialSuffix(drive.Serial);

        if (slug.Length == 0)
        {
            slug = "disk";
        }

        return suffix.Length == 0 ? $"storage.{slug}" : $"storage.{slug}-{suffix}";
    }

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        bool lastWasDash = false;

        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string SerialSuffix(string serial)
    {
        var letters = new StringBuilder(4);

        for (int index = serial.Length - 1; index >= 0 && letters.Length < 4; index--)
        {
            if (char.IsAsciiLetterOrDigit(serial[index]))
            {
                letters.Insert(0, char.ToLowerInvariant(serial[index]));
            }
        }

        return letters.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
}
