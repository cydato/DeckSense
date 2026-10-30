namespace DeckSense.Service.Sensors;

public enum SensorCategory
{
    Cpu,
    Memory,
    Gpu,
    Storage,
    Rtss
}

/// <summary>
/// Beschreibt einen Sensor unveränderlich. Die <see cref="Id"/> ist stabil und
/// darf sich nach einer Veröffentlichung nicht mehr ändern - die
/// Tastenzuordnungen im Stream Deck hängen daran.
/// </summary>
/// <param name="Id">Stabile Kennung, z. B. <c>cpu.temp</c>.</param>
/// <param name="Name">Vorschlag für den Tastentitel, im Plugin überschreibbar.</param>
/// <param name="Unit">Einheit zur Anzeige, z. B. <c>%</c>, <c>°C</c>, <c>MB</c>.</param>
/// <param name="Haltezeit">
/// Wie lange der letzte gemessene Wert weitergereicht werden darf, wenn eine
/// Messung ausfällt. <see cref="TimeSpan.Zero"/> heißt: gar nicht, ein Ausfall
/// schlägt sofort auf die Anzeige durch.
///
/// <para>
/// Gedacht gegen kurze Aussetzer, die keine Aussage sind: Eine
/// Festplatte im Ruhezustand beantwortet die Temperaturabfrage nicht, ein
/// Leistungsindikator verschluckt sich für einen Takt. Siehe Entscheidung 0025.
/// </para>
///
/// <para>
/// <b>Nicht</b> gedacht für Werte, deren Fehlen eine echte Aussage ist: Ohne
/// laufendes Spiel gibt es keine FPS, und eine gehaltene Bildrate wäre dann
/// schlicht falsch. Diese Sensoren behalten <see cref="TimeSpan.Zero"/>.
/// </para>
/// </param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    SensorCategory Category,
    string Unit,
    TimeSpan Haltezeit = default);

/// <summary>
/// Vorgaben für <see cref="SensorDescriptor.Haltezeit"/>, an einer Stelle
/// festgelegt, damit sie vergleichbar bleiben.
/// </summary>
public static class Haltezeiten
{
    /// <summary>
    /// Für den schnellen Takt (1 s). Deckt den einzelnen verschluckten Takt
    /// eines Leistungsindikators ab, mehr nicht.
    /// </summary>
    public static readonly TimeSpan Schnell = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Für Laufwerke (Takt 15 s). Gemessen an dieser Maschine dauern die
    /// Aussetzer der beiden 3-TB-Festplatten einen bis drei Takte, also 15 bis
    /// 45 Sekunden. 60 Sekunden decken das ab und sind kurz genug, dass ein
    /// wirklich verschwundenes Laufwerk schnell auffällt.
    /// </summary>
    public static readonly TimeSpan Laufwerke = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Ein Messwert zu einem Zeitpunkt. <see cref="Value"/> ist genau dann
/// <c>null</c>, wenn <see cref="Available"/> <c>false</c> ist - ein fehlender
/// Wert ist nie <c>0</c>.
/// </summary>
/// <param name="UpdatedAt">
/// Zeitpunkt der letzten <b>echten</b> Messung - bei einem gehaltenen Wert
/// also nicht „jetzt". Daran bemisst sich die Haltezeit, und das Plugin kann
/// daran ablesen, wie alt eine Zahl ist.
/// </param>
/// <param name="Stale">
/// Der Wert ist gehalten: Die letzte Messung ist fehlgeschlagen, der hier
/// genannte Wert stammt von davor. <see cref="Available"/> bleibt
/// <c>true</c>, damit die Anzeige nicht flackert - aber die API sagt es dazu.
/// </param>
public sealed record SensorReading(
    SensorDescriptor Descriptor,
    double? Value,
    bool Available,
    DateTimeOffset UpdatedAt,
    bool Stale = false)
{
    public static SensorReading Ok(SensorDescriptor descriptor, double value, DateTimeOffset at)
        => new(descriptor, value, true, at);

    public static SensorReading Unavailable(SensorDescriptor descriptor, DateTimeOffset at)
        => new(descriptor, null, false, at);

    /// <summary>
    /// Für die Diagnoseausgaben. Ein gehaltener Wert wird als solcher
    /// gekennzeichnet - auf der Taste soll er unauffällig sein, in einer
    /// Messausgabe gerade nicht.
    /// </summary>
    public string Format()
    {
        if (!Available || !Value.HasValue)
        {
            return "--";
        }

        string wert = FormattableString.Invariant($"{Value.Value:0.#} {Descriptor.Unit}");

        return Stale ? wert + " (gehalten)" : wert;
    }
}
