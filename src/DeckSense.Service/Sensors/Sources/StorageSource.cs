using System.Management;
using DeckSense.Service.Configuration;
using DeckSense.Service.Interop;
using DeckSense.Service.Logging;

namespace DeckSense.Service.Sensors.Sources;

/// <summary>
/// Laufwerkstemperaturen über die Windows-eigene Storage-Verwaltung.
///
/// Gelesen wird je Laufwerk <b>einzeln</b>: von einer bestimmten Instanz von
/// <c>MSFT_PhysicalDisk</c> über die Association
/// <c>MSFT_PhysicalDiskToStorageReliabilityCounter</c> zum zugehörigen
/// <c>MSFT_StorageReliabilityCounter</c>. Genau diesen Weg nimmt auch das
/// Cmdlet <c>Get-StorageReliabilityCounter</c> - nachgesehen in
/// <c>StorageReliabilityCounter.cdxml</c> des Windows-Storage-Moduls.
///
/// Wichtig für dieses Projekt: Es gibt <b>keine</b> Abfrage über alle
/// Laufwerke. Aufgezählt werden nur Geräteeigenschaften (Modell, Seriennummer)
/// - das liest keine SMART-Daten. Der Temperaturzugriff erfolgt ausschließlich
/// auf den Instanzen, die in der Freigabeliste stehen.
/// </summary>
public sealed class StorageSource : ISensorSource
{
    private const string AssociationClass = "MSFT_PhysicalDiskToStorageReliabilityCounter";
    private const string CounterClass = "MSFT_StorageReliabilityCounter";
    private const string CounterRole = "StorageReliabilityCounter";
    private const string DiskRole = "PhysicalDisk";

    /// <summary>
    /// Plausibilitätsfenster. 0 °C ist bei einem laufenden Laufwerk kein
    /// Messwert, sondern ein nicht gefülltes Feld.
    /// </summary>
    private const byte MinimumPlausible = 5;

    private const byte MaximumPlausible = 120;

    private static readonly TimeSpan ReResolveDelay = TimeSpan.FromMinutes(1);

    private readonly ILog _log;
    private readonly List<SensorDescriptor> _descriptors = [];

    /// <summary>Normalisierte Seriennummer je Sensor-ID.</summary>
    private readonly Dictionary<string, string> _serialByDescriptorId = new(StringComparer.Ordinal);

    /// <summary>WMI-Objektpfad je Sensor-ID, erst nach dem Auflösen gefüllt.</summary>
    private readonly Dictionary<string, string> _pathByDescriptorId = new(StringComparer.Ordinal);

    private DateTimeOffset _nextResolveAttempt = DateTimeOffset.MinValue;
    private bool _disposed;

    public StorageSource(ILog log, StorageConfig config)
    {
        _log = log;

        HashSet<string> blocked = [.. config.Excluded
            .Select(device => Normalize(device.Serial))
            .Where(serial => serial.Length > 0)];

        foreach (StorageDeviceConfig device in config.Allowed)
        {
            string serial = Normalize(device.Serial);

            if (string.IsNullOrWhiteSpace(device.Id) || serial.Length == 0)
            {
                _log.Warn($"Laufwerkseintrag ohne Id oder Seriennummer wird übergangen: '{device.Name}'.");
                continue;
            }

            if (blocked.Contains(serial))
            {
                // Die Sperre hat Vorrang. Das ist der wichtigste Satz dieser Klasse.
                _log.Error($"Laufwerk '{device.Name}' ({device.Id}) steht in der Freigabe- UND in der "
                           + "Ausschlussliste. Die Ausschlussliste hat Vorrang, das Laufwerk wird NICHT abgefragt.");
                continue;
            }

            var descriptor = new SensorDescriptor(
                $"{device.Id}.temp", device.Name, SensorCategory.Storage, "°C", Haltezeiten.Laufwerke);
            _descriptors.Add(descriptor);
            _serialByDescriptorId[descriptor.Id] = serial;
        }
    }

    public string Name => "Laufwerkstemperaturen (Storage-WMI)";

    public IReadOnlyList<SensorDescriptor> Descriptors => _descriptors;

    public void Initialize()
    {
        if (_descriptors.Count == 0)
        {
            _log.Info("Keine Laufwerke freigegeben. Es wird kein Laufwerk angefasst.");
            return;
        }

        _log.Info($"{_descriptors.Count} Laufwerke freigegeben: "
                  + string.Join(", ", _descriptors.Select(d => d.Id)));

        Resolve();
    }

    public IReadOnlyList<SensorReading> Read(DateTimeOffset at)
    {
        if (_descriptors.Count == 0)
        {
            return [];
        }

        if (_pathByDescriptorId.Count < _descriptors.Count && DateTimeOffset.UtcNow >= _nextResolveAttempt)
        {
            Resolve();
        }

        var readings = new List<SensorReading>(_descriptors.Count);

        // Bewusst sequenziell, ein Laufwerk nach dem anderen.
        foreach (SensorDescriptor descriptor in _descriptors)
        {
            readings.Add(ReadOne(descriptor, at));
        }

        return readings;
    }

    private SensorReading ReadOne(SensorDescriptor descriptor, DateTimeOffset at)
    {
        if (!_pathByDescriptorId.TryGetValue(descriptor.Id, out string? path))
        {
            return SensorReading.Unavailable(descriptor, at);
        }

        try
        {
            using var disk = new ManagementObject(new ManagementPath(path));
            using ManagementObjectCollection related = disk.GetRelated(
                CounterClass,
                AssociationClass,
                relationshipQualifier: null,
                relatedQualifier: null,
                relatedRole: CounterRole,
                thisRole: DiskRole,
                classDefinitionsOnly: false,
                options: null);

            foreach (ManagementBaseObject counter in related)
            {
                using (counter)
                {
                    object? raw = counter["Temperature"];
                    if (raw is null)
                    {
                        continue;
                    }

                    byte celsius = Convert.ToByte(raw, System.Globalization.CultureInfo.InvariantCulture);
                    if (celsius < MinimumPlausible || celsius > MaximumPlausible)
                    {
                        continue;
                    }

                    return SensorReading.Ok(descriptor, celsius, at);
                }
            }
        }
        catch (Exception error)
        {
            // Der Pfad kann veraltet sein, etwa nach einem Neustart des
            // Storage-Dienstes. Dann beim nächsten Mal neu auflösen.
            _log.Warn($"Temperatur von {descriptor.Id} konnte nicht gelesen werden.", error);
            _pathByDescriptorId.Remove(descriptor.Id);
            _nextResolveAttempt = DateTimeOffset.UtcNow + ReResolveDelay;
        }

        return SensorReading.Unavailable(descriptor, at);
    }

    /// <summary>
    /// Ordnet den freigegebenen Seriennummern die WMI-Objektpfade zu. Dabei
    /// werden nur Geräteeigenschaften gelesen, keine SMART-Daten.
    /// </summary>
    private void Resolve()
    {
        _nextResolveAttempt = DateTimeOffset.UtcNow + ReResolveDelay;

        try
        {
            foreach (StorageDeviceInfo disk in StorageDetector.Enumerate())
            {
                foreach ((string descriptorId, string wanted) in _serialByDescriptorId)
                {
                    if (!string.Equals(disk.Serial, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!_pathByDescriptorId.ContainsKey(descriptorId))
                    {
                        _log.Info($"{descriptorId} zugeordnet: '{disk.FriendlyName}' "
                                  + $"(Laufwerk {disk.DeviceNumber}, {disk.MediaType}, {disk.BusType}).");
                    }

                    _pathByDescriptorId[descriptorId] = disk.WmiPath;
                    break;
                }
            }

            foreach (SensorDescriptor descriptor in _descriptors)
            {
                if (!_pathByDescriptorId.ContainsKey(descriptor.Id))
                {
                    _log.Warn($"{descriptor.Id}: kein Laufwerk mit der konfigurierten Seriennummer gefunden. "
                              + "Der Sensor bleibt nicht verfügbar.");
                }
            }
        }
        catch (Exception error)
        {
            _log.Error("Die Laufwerke konnten nicht zugeordnet werden. "
                       + "Ohne Admin-Rechte ist das zu erwarten.", error);
        }
    }

    private static string Normalize(string? serial) => StorageDetector.Normalize(serial);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pathByDescriptorId.Clear();
    }
}
