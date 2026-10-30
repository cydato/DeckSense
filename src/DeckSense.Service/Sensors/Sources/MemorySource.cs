using DeckSense.Service.Interop;

namespace DeckSense.Service.Sensors.Sources;

/// <summary>
/// Belegter Arbeitsspeicher in MB, aus <c>GlobalMemoryStatusEx</c>.
/// Belegt = physisch gesamt minus physisch verfügbar.
/// </summary>
public sealed class MemorySource : ISensorSource
{
    public static readonly SensorDescriptor Used =
        new("mem.used", "RAM Used", SensorCategory.Memory, "MB");

    private const double BytesPerMegabyte = 1024d * 1024d;

    public string Name => "Arbeitsspeicher (GlobalMemoryStatusEx)";

    public IReadOnlyList<SensorDescriptor> Descriptors => [Used];

    public void Initialize()
    {
        // Ein Probeaufruf, damit ein Problem beim Start auffällt und nicht erst
        // im ersten Takt.
        NativeMemoryStatus.Query();
    }

    public IReadOnlyList<SensorReading> Read(DateTimeOffset at)
    {
        NativeMemoryStatus.MemoryStatusEx status = NativeMemoryStatus.Query();

        if (status.TotalPhys == 0 || status.AvailPhys > status.TotalPhys)
        {
            return [SensorReading.Unavailable(Used, at)];
        }

        double usedMegabytes = (status.TotalPhys - status.AvailPhys) / BytesPerMegabyte;
        return [SensorReading.Ok(Used, usedMegabytes, at)];
    }

    public void Dispose()
    {
        // Nichts zu schließen.
    }
}
