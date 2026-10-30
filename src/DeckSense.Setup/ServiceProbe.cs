using System.Diagnostics;
using System.Text.Json;

namespace DeckSense.Setup;

internal sealed record SensorZeile(string Id, string Name, string Wert);

internal sealed record DienstStand(
    bool ProzessLaeuft,
    bool ApiErreichbar,
    string? Version,
    IReadOnlyList<SensorZeile> Sensoren);

/// <summary>
/// Fragt ab, wie es um den Dienst steht: läuft der Prozess, antwortet die API,
/// welche Werte kommen. Nur lesend - die Oberfläche ändert nichts von selbst.
/// </summary>
internal static class ServiceProbe
{
    internal const string ProzessName = "DeckSense.Service";
    private const string Basis = "http://127.0.0.1:5891";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMilliseconds(900) };

    internal static bool ProzessLaeuft()
        => Process.GetProcessesByName(ProzessName).Length > 0;

    internal static async Task<DienstStand> AbfragenAsync()
    {
        bool laeuft = ProzessLaeuft();

        try
        {
            string json = await Client.GetStringAsync($"{Basis}/api/sensors").ConfigureAwait(false);

            using JsonDocument dokument = JsonDocument.Parse(json);
            JsonElement wurzel = dokument.RootElement;

            string? version = wurzel.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
            var zeilen = new List<SensorZeile>();

            if (wurzel.TryGetProperty("sensors", out JsonElement sensoren))
            {
                foreach (JsonElement sensor in sensoren.EnumerateArray())
                {
                    string id = sensor.GetProperty("id").GetString() ?? "?";
                    string name = sensor.GetProperty("name").GetString() ?? "?";
                    string einheit = sensor.GetProperty("unit").GetString() ?? string.Empty;
                    bool verfuegbar = sensor.GetProperty("available").GetBoolean();

                    string wert = "--";
                    if (verfuegbar && sensor.TryGetProperty("value", out JsonElement w) && w.ValueKind == JsonValueKind.Number)
                    {
                        double zahl = w.GetDouble();
                        wert = einheit == "%"
                            ? $"{Math.Round(zahl)}%"
                            : $"{Math.Round(zahl)} {einheit}";
                    }

                    zeilen.Add(new SensorZeile(id, name, wert));
                }
            }

            return new DienstStand(laeuft, true, version, zeilen);
        }
        catch (Exception)
        {
            // Dienst läuft nicht, startet gerade, oder die Anfrage lief ins
            // Zeitlimit. Kein Grund für eine Fehlermeldung - das Fenster zeigt
            // es einfach an.
            return new DienstStand(laeuft, false, null, []);
        }
    }

    /// <summary>
    /// Sucht die Exe des Dienstes. Im ausgelieferten Archiv liegt sie neben der
    /// Oberfläche; in der Entwicklung in ihrem eigenen Ausgabeordner.
    /// </summary>
    internal static string? ExeFinden()
    {
        string hier = AppContext.BaseDirectory;

        string[] kandidaten =
        [
            Path.Combine(hier, "DeckSense.Service.exe"),
            Path.Combine(hier, "..", "..", "..", "..", "DeckSense.Service", "bin", "Debug", "net10.0-windows", "DeckSense.Service.exe"),
            Path.Combine(hier, "..", "..", "..", "..", "DeckSense.Service", "bin", "Release", "net10.0-windows", "DeckSense.Service.exe"),
        ];

        foreach (string kandidat in kandidaten)
        {
            try
            {
                string voll = Path.GetFullPath(kandidat);
                if (File.Exists(voll))
                {
                    return voll;
                }
            }
            catch (Exception)
            {
                // Ungültiger Pfad, nächster Kandidat.
            }
        }

        return null;
    }

    internal static void Starten(string exePfad)
    {
        Process.Start(new ProcessStartInfo(exePfad)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePfad) ?? string.Empty,
        });
    }

    /// <summary>
    /// Beendet alle laufenden Dienstprozesse.
    ///
    /// <para>
    /// Gibt zurück, ob danach <b>keiner mehr läuft</b>. Das ist nicht
    /// selbstverständlich: Der Dienst läuft mit Administratorrechten, und
    /// <c>Kill</c> aus einem nicht erhöhten Prozess scheitert dann mit
    /// „Zugriff verweigert". Vorher wurde die Ausnahme verschluckt und die
    /// Oberfläche meldete „Dienst beendet.", während er weiterlief.
    /// </para>
    /// </summary>
    internal static bool Beenden(string? nurDiese = null)
    {
        foreach (Process prozess in Process.GetProcessesByName(ProzessName))
        {
            using (prozess)
            {
                if (nurDiese is not null && !PfadPasst(prozess, nurDiese))
                {
                    continue;
                }

                try
                {
                    prozess.Kill();
                    prozess.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Schon beendet, oder keine Berechtigung. Was davon, sagt
                    // die Nachkontrolle unten.
                }
            }
        }

        // Nachsehen statt annehmen.
        return nurDiese is null ? !ProzessLaeuft() : !LaeuftAus(nurDiese);
    }

    /// <summary>Läuft ein Dienst aus genau dieser Exe?</summary>
    internal static bool LaeuftAus(string exePfad)
    {
        foreach (Process prozess in Process.GetProcessesByName(ProzessName))
        {
            using (prozess)
            {
                if (PfadPasst(prozess, exePfad))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Läuft dieser Prozess aus der angegebenen Exe?
    ///
    /// <para>
    /// Lässt sich der Pfad nicht lesen, gilt er als <b>passend</b>. Das ist die
    /// vorsichtige Richtung für den einzigen Zweck dieser Abfrage: Vor dem
    /// Überschreiben soll kein Prozess übrig bleiben, der die Datei sperrt. Ein
    /// Prozess, dessen Pfad verborgen ist, wird also mitgezählt.
    /// </para>
    /// </summary>
    private static bool PfadPasst(Process prozess, string exePfad)
    {
        try
        {
            string? laeuftAus = prozess.MainModule?.FileName;

            if (string.IsNullOrEmpty(laeuftAus))
            {
                return true;
            }

            return string.Equals(
                Path.GetFullPath(laeuftAus),
                Path.GetFullPath(exePfad),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Kein Zugriff auf das Modul - etwa weil der Prozess erhöht läuft
            // und wir es nicht sind. Als passend behandeln, siehe oben.
            return true;
        }
    }
}
