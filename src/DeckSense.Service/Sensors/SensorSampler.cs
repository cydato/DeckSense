using DeckSense.Service.Logging;

namespace DeckSense.Service.Sensors;

/// <summary>
/// Ein Abfragetakt. Der Dienst betreibt zwei davon: den schnellen mit 1 s und
/// den für Laufwerke mit 15 s.
///
/// Innerhalb eines Takts werden die Quellen <b>sequenziell</b> gelesen - /// gleichzeitige Hardwareabfragen sind ein bekannter Auslöser für DPC-Spitzen,
/// und genau die soll dieses Werkzeug nicht verursachen.
///
/// Läuft ein Takt noch, wird der nächste übersprungen statt eingereiht. Ein
/// ausgelassener Wert ist harmloser als eine Warteschlange.
/// </summary>
public sealed class SensorSampler : IDisposable
{
    private readonly string _name;
    private readonly ISensorSource[] _sources;
    private readonly SnapshotStore _store;
    private readonly ILog _log;
    private readonly TimeSpan _interval;
    private readonly bool _sampleImmediately;
    private readonly Dictionary<string, bool> _lastAvailable = new(StringComparer.Ordinal);

    private Timer? _timer;
    private int _busy;
    private long _skippedTicks;
    private bool _disposed;

    /// <param name="sampleImmediately">
    /// Sofort beim Start einmal messen, statt den ersten Takt abzuwarten.
    ///
    /// Für den schnellen Takt ist das <b>falsch</b>: Ratenindikatoren wie die
    /// Prozessorzeit brauchen zwei Messpunkte mit Abstand, über wenige
    /// Millisekunden gemessen kommt Unsinn heraus (siehe Entscheidung 0009).
    /// Für die Laufwerke ist es richtig - eine Temperatur ist ein Momentanwert,
    /// und 15 Sekunden „nicht verfügbar" nach jedem Dienststart wären unnötig.
    /// </param>
    public SensorSampler(
        string name,
        IEnumerable<ISensorSource> sources,
        SnapshotStore store,
        ILog log,
        TimeSpan interval,
        bool sampleImmediately = false)
    {
        _name = name;
        _sampleImmediately = sampleImmediately;
        _sources = [.. sources];
        _store = store;
        _log = log;
        _interval = interval;

        foreach (ISensorSource source in _sources)
        {
            foreach (SensorDescriptor descriptor in source.Descriptors)
            {
                // Startannahme „verfügbar": Dadurch wird jeder Ausfall genau
                // einmal protokolliert, statt in jedem Takt erneut.
                _lastAvailable[descriptor.Id] = true;
            }
        }
    }

    public long SkippedTicks => Interlocked.Read(ref _skippedTicks);

    public bool HasSources => _sources.Length > 0;

    public void Start()
    {
        if (!HasSources)
        {
            return;
        }

        // Ohne Sofort-Sample steht zunächst ein Stand mit „nicht verfügbar"
        // bereit, und der erste echte Wert kommt nach einem vollen Takt.
        PublishStartupState(DateTimeOffset.UtcNow);

        if (_sampleImmediately)
        {
            SampleOnce();
        }

        _timer = new Timer(_ => SampleOnce(), null, _interval, _interval);
    }

    /// <summary>
    /// Alle Sensoren dieses Takts als „nicht verfügbar". So hat der
    /// Zwischenspeicher von der ersten Sekunde an die richtige Form, ohne
    /// erfundene Werte.
    /// </summary>
    private void PublishStartupState(DateTimeOffset at)
    {
        var readings = new List<SensorReading>(_lastAvailable.Count);

        foreach (ISensorSource source in _sources)
        {
            foreach (SensorDescriptor descriptor in source.Descriptors)
            {
                readings.Add(SensorReading.Unavailable(descriptor, at));
            }
        }

        _store.Publish(at, readings);
    }

    /// <summary>
    /// Ein Durchlauf über alle Quellen dieses Takts. Öffentlich, damit der
    /// Diagnosemodus gezielt messen kann.
    /// </summary>
    public void SampleOnce()
    {
        if (!HasSources)
        {
            return;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            Interlocked.Increment(ref _skippedTicks);
            return;
        }

        try
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            var readings = new List<SensorReading>(_lastAvailable.Count);

            foreach (ISensorSource source in _sources)
            {
                try
                {
                    readings.AddRange(source.Read(at));
                }
                catch (Exception error)
                {
                    // Eine kaputte Quelle nimmt nur ihre eigenen Sensoren mit.
                    _log.Warn($"Quelle '{source.Name}' hat beim Lesen gefehlt.", error);
                    foreach (SensorDescriptor descriptor in source.Descriptors)
                    {
                        readings.Add(SensorReading.Unavailable(descriptor, at));
                    }
                }
            }

            LogAvailabilityChanges(readings);
            _store.Publish(at, readings);
        }
        catch (Exception error)
        {
            // Hierher darf es eigentlich nicht kommen. Falls doch: protokollieren
            // und weiterlaufen, niemals den Dienst beenden.
            _log.Error($"Unerwarteter Fehler im Takt '{_name}'.", error);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void LogAvailabilityChanges(List<SensorReading> readings)
    {
        foreach (SensorReading reading in readings)
        {
            string id = reading.Descriptor.Id;
            if (_lastAvailable.TryGetValue(id, out bool previous) && previous == reading.Available)
            {
                continue;
            }

            _lastAvailable[id] = reading.Available;
            _log.Info(reading.Available
                ? $"Sensor {id} liefert wieder Werte."
                : $"Sensor {id} ist nicht verfügbar.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;

        foreach (ISensorSource source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception error)
            {
                _log.Warn($"Quelle '{source.Name}' ließ sich nicht sauber schließen.", error);
            }
        }
    }
}
