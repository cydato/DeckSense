namespace DeckSense.Service.Sensors;

/// <summary>
/// Unveränderliche Momentaufnahme aller Sensorwerte. Wird als Ganzes
/// ausgetauscht, damit Leser nie einen halb aktualisierten Stand sehen.
/// </summary>
public sealed class SensorSnapshot
{
    public static SensorSnapshot Empty { get; } =
        new(DateTimeOffset.MinValue, Array.Empty<SensorReading>());

    private readonly Dictionary<string, SensorReading> _byId;

    public SensorSnapshot(DateTimeOffset generatedAt, IReadOnlyList<SensorReading> readings)
    {
        GeneratedAt = generatedAt;
        Readings = readings;
        _byId = new Dictionary<string, SensorReading>(readings.Count, StringComparer.Ordinal);

        foreach (SensorReading reading in readings)
        {
            _byId[reading.Descriptor.Id] = reading;
        }
    }

    public DateTimeOffset GeneratedAt { get; }

    public IReadOnlyList<SensorReading> Readings { get; }

    public bool TryGet(string id, out SensorReading reading)
        => _byId.TryGetValue(id, out reading!);
}

/// <summary>
/// Hält den jeweils letzten Snapshot über alle Takte hinweg.
///
/// Mehrere Sampler schreiben hier hinein - der schnelle Takt mit 1 s und der
/// Laufwerkstakt mit 15 s. Jeder liefert nur seine eigenen Sensoren; der
/// Snapshot ist die Vereinigung. Geschrieben wird unter einer Sperre, gelesen
/// ohne: Der Lesepfad darf nie auf etwas warten, und spätere API-Aufrufe lesen
/// ausschließlich hier.
/// </summary>
public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SensorReading> _readings = new(StringComparer.Ordinal);

    /// <summary>
    /// Reihenfolge der ersten Anmeldung. Damit bleibt die Ausgabe stabil und
    /// hängt nicht an der Laune eines Dictionary.
    /// </summary>
    private readonly List<string> _order = [];

    private SensorSnapshot _current = SensorSnapshot.Empty;

    public SensorSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Entscheidet, ob ein ausgefallener Messwert durch den letzten guten
    /// ersetzt wird.
    ///
    /// <para>
    /// Gehalten wird nur, solange die Haltezeit des Sensors nicht abgelaufen
    /// ist, gerechnet ab der letzten <b>echten</b> Messung. Deren Zeitstempel
    /// bleibt deshalb stehen: Er ist die Wahrheit über das Alter der Zahl, und
    /// nur mit ihm läuft die Haltezeit überhaupt ab. Siehe Entscheidung 0025.
    /// </para>
    /// </summary>
    private static SensorReading Halten(SensorReading? bisher, SensorReading neu)
    {
        if (neu.Available)
        {
            return neu;
        }

        if (bisher is null || !bisher.Available)
        {
            return neu;
        }

        TimeSpan haltezeit = neu.Descriptor.Haltezeit;

        if (haltezeit <= TimeSpan.Zero)
        {
            return neu;
        }

        return neu.UpdatedAt - bisher.UpdatedAt <= haltezeit
            ? bisher with { Stale = true }
            : neu;
    }

    /// <summary>
    /// Übernimmt die Messungen eines Takts und baut daraus einen neuen
    /// Snapshot. Werte anderer Sampler bleiben unverändert stehen.
    /// </summary>
    public void Publish(DateTimeOffset generatedAt, IReadOnlyList<SensorReading> readings)
    {
        lock (_gate)
        {
            foreach (SensorReading reading in readings)
            {
                string id = reading.Descriptor.Id;

                if (!_readings.ContainsKey(id))
                {
                    _order.Add(id);
                }

                _readings.TryGetValue(id, out SensorReading? bisher);
                _readings[id] = Halten(bisher, reading);
            }

            var ordered = new List<SensorReading>(_order.Count);
            foreach (string id in _order)
            {
                ordered.Add(_readings[id]);
            }

            Volatile.Write(ref _current, new SensorSnapshot(generatedAt, ordered));
        }
    }
}
