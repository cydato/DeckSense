namespace DeckSense.Service.Sensors;

/// <summary>
/// Eine Quelle liefert einen oder mehrere Sensoren. Sie darf aus
/// <see cref="Initialize"/> und <see cref="Read"/> werfen - der Sampler fängt
/// das ab und meldet die betroffenen Sensoren als nicht verfügbar. Der Dienst
/// endet dadurch nie.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Name für das Protokoll, nicht für die Anzeige.</summary>
    string Name { get; }

    /// <summary>Alle Sensoren, die diese Quelle liefern kann.</summary>
    IReadOnlyList<SensorDescriptor> Descriptors { get; }

    void Initialize();

    /// <summary>
    /// Liest alle Sensoren dieser Quelle. Fällt ein einzelner Sensor aus,
    /// liefert die Quelle für ihn selbst eine nicht verfügbare Messung; der
    /// Rest bleibt gültig.
    /// </summary>
    IReadOnlyList<SensorReading> Read(DateTimeOffset at);
}
