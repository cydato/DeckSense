using DeckSense.Service.Interop;
using DeckSense.Service.Logging;

namespace DeckSense.Service.Sensors.Sources;

/// <summary>
/// Liest die beiden CPU-Werte über PDH.
///
/// <c>cpu.usage</c> ist die klassische Prozessorzeit, bei 100 % abgeschnitten.
/// <c>cpu.utility</c> ist der taktbereinigte Wert, den auch der Task-Manager
/// zeigt; er darf über 100 % liegen und wird deshalb ohne Deckel formatiert.
/// </summary>
public sealed class CpuCounterSource : ISensorSource
{
    public static readonly SensorDescriptor Usage =
        new("cpu.usage", "CPU Usage", SensorCategory.Cpu, "%", Haltezeiten.Schnell);

    public static readonly SensorDescriptor Utility =
        new("cpu.utility", "CPU Utility", SensorCategory.Cpu, "%", Haltezeiten.Schnell);

    private const string UsagePath = @"\Processor(_Total)\% Processor Time";
    private const string UtilityPath = @"\Processor Information(_Total)\% Processor Utility";

    private readonly ILog _log;

    private nint _query;
    private nint _usageCounter;
    private nint _utilityCounter;
    private bool _disposed;

    public CpuCounterSource(ILog log) => _log = log;

    public string Name => "CPU-Leistungsindikatoren (PDH)";

    public IReadOnlyList<SensorDescriptor> Descriptors => [Usage, Utility];

    public void Initialize()
    {
        uint status = Pdh.OpenQuery(null, 0, out _query);
        if (status != 0)
        {
            throw new InvalidOperationException($"PdhOpenQuery ist fehlgeschlagen: 0x{status:X8}");
        }

        _usageCounter = AddCounter(UsagePath, Usage.Id);
        _utilityCounter = AddCounter(UtilityPath, Utility.Id);

        if (_usageCounter == 0 && _utilityCounter == 0)
        {
            throw new InvalidOperationException("Keiner der beiden CPU-Indikatoren ist verfügbar.");
        }

        // Ratenindikatoren brauchen zwei Messpunkte. Den ersten holen wir hier,
        // damit der erste Takt bereits einen gültigen Wert liefert.
        Pdh.CollectQueryData(_query);
    }

    private nint AddCounter(string path, string sensorId)
    {
        uint status = Pdh.AddEnglishCounter(_query, path, 0, out nint counter);
        if (status == 0)
        {
            return counter;
        }

        _log.Warn($"Indikator '{path}' ist nicht verfügbar (0x{status:X8}). {sensorId} bleibt dauerhaft nicht verfügbar.");
        return 0;
    }

    public IReadOnlyList<SensorReading> Read(DateTimeOffset at)
    {
        uint status = Pdh.CollectQueryData(_query);
        if (status != 0)
        {
            return [SensorReading.Unavailable(Usage, at), SensorReading.Unavailable(Utility, at)];
        }

        return
        [
            ReadOne(_usageCounter, Usage, Pdh.FormatDouble, at),
            ReadOne(_utilityCounter, Utility, Pdh.FormatDouble | Pdh.FormatNoCap100, at)
        ];
    }

    private static SensorReading ReadOne(nint counter, SensorDescriptor descriptor, uint format, DateTimeOffset at)
    {
        if (counter == 0)
        {
            return SensorReading.Unavailable(descriptor, at);
        }

        // Beim allerersten Messpunkt liefert PDH hier InvalidData. Das ist kein
        // Fehler, sondern „noch kein zweiter Messpunkt" - also nicht verfügbar.
        uint status = Pdh.GetFormattedCounterValue(counter, format, out _, out Pdh.FormattedCounterValue value);
        if (status != 0 || !Pdh.IsUsableStatus(value.CStatus))
        {
            return SensorReading.Unavailable(descriptor, at);
        }

        double result = value.DoubleValue;
        if (double.IsNaN(result) || double.IsInfinity(result) || result < 0)
        {
            return SensorReading.Unavailable(descriptor, at);
        }

        return SensorReading.Ok(descriptor, result, at);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_query != 0)
        {
            // Schließt die Abfrage samt aller daran hängenden Indikatoren.
            Pdh.CloseQuery(_query);
            _query = 0;
        }
    }
}
