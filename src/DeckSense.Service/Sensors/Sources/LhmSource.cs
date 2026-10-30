using DeckSense.Service.Configuration;
using DeckSense.Service.Interop;
using DeckSense.Service.Logging;
using LibreHardwareMonitor.Hardware;

namespace DeckSense.Service.Sensors.Sources;

/// <summary>
/// CPU-Temperatur und GPU-Werte über LibreHardwareMonitorLib 0.9.6.
///
/// Freigegeben sind ausschließlich die Gruppen <b>CPU</b> und <b>GPU</b>.
/// Storage bleibt aus und kommt erst mit Version 0.5.0 - geprüft am Quellstand
/// v0.9.6: <c>AddGroups()</c> konstruiert <c>StorageGroup</c> nur innerhalb von
/// <c>if (_storageEnabled)</c>, ein abgeschaltetes Flag fasst also kein
/// Laufwerk an. Das ist für dieses Projekt die entscheidende Zusage.
///
/// <c>Update()</c> wird gezielt nur auf der freigegebenen Hardware gerufen,
/// nie global über alle Geräte.
/// </summary>
public sealed class LhmSource : ISensorSource
{
    public static readonly SensorDescriptor CpuTemp =
        new("cpu.temp", "CPU Temp", SensorCategory.Cpu, "°C", Haltezeiten.Schnell);

    public static readonly SensorDescriptor GpuLoad =
        new("gpu.load", "GPU Load", SensorCategory.Gpu, "%", Haltezeiten.Schnell);

    public static readonly SensorDescriptor GpuTemp =
        new("gpu.temp", "GPU Temp", SensorCategory.Gpu, "°C", Haltezeiten.Schnell);

    public static readonly SensorDescriptor GpuFan =
        new("gpu.fan", "GPU Fan", SensorCategory.Gpu, "%", Haltezeiten.Schnell);

    /// <summary>
    /// Belegter Grafikspeicher. Bei zwölf Gigabyte ist das die Zahl, an der
    /// moderne Spiele anfangen zu ruckeln - neben FPS und Frametime steht
    /// damit auch das Warum auf einer Taste.
    /// </summary>
    public static readonly SensorDescriptor GpuVram =
        new("gpu.vram", "VRAM Used", SensorCategory.Gpu, "MB", Haltezeiten.Schnell);

    public static readonly SensorDescriptor GpuPower =
        new("gpu.power", "GPU Watt", SensorCategory.Gpu, "W", Haltezeiten.Schnell);

    /// <summary>
    /// Der heißeste Punkt auf dem Chip. Drosselt wird danach, nicht nach
    /// „GPU Core" - auf dieser Maschine liegen rund zehn Grad dazwischen.
    /// </summary>
    public static readonly SensorDescriptor GpuHotspot =
        new("gpu.hotspot", "GPU Hot Spot", SensorCategory.Gpu, "°C", Haltezeiten.Schnell);

    public static readonly SensorDescriptor GpuVramTemp =
        new("gpu.vram.temp", "VRAM Temp", SensorCategory.Gpu, "°C", Haltezeiten.Schnell);

    /// <summary>
    /// Unterhalb dieser Temperatur gilt ein Messwert als Fehler, nicht als
    /// Messung. Ohne PawnIO liefert die Bibliothek stillschweigend Nullen,
    /// siehe Entscheidung 0007. Eine CPU, die läuft, ist nicht 1 °C warm.
    /// </summary>
    private const float MinimumPlausibleTemperature = 5f;

    private readonly ILog _log;
    private readonly HardwareConfig _hardwareConfig;

    private Computer? _computer;
    private IHardware[] _hardware = [];
    private ISensor? _cpuTempSensor;
    private ISensor? _gpuLoadSensor;
    private ISensor? _gpuTempSensor;
    private ISensor? _gpuFanSensor;
    private ISensor? _gpuVramSensor;
    private ISensor? _gpuPowerSensor;
    private ISensor? _gpuHotspotSensor;
    private ISensor? _gpuVramTempSensor;
    private bool _sensorsResolved;
    private bool _disposed;

    public LhmSource(ILog log, HardwareConfig hardwareConfig)
    {
        _log = log;
        _hardwareConfig = hardwareConfig;
    }

    public string Name
    {
        get
        {
            List<string> groups = [];
            if (_hardwareConfig.Cpu)
            {
                groups.Add("CPU");
            }

            if (_hardwareConfig.Gpu)
            {
                groups.Add("GPU");
            }

            if (_hardwareConfig.Storage)
            {
                groups.Add("Storage");
            }

            return groups.Count == 0
                ? "LibreHardwareMonitor (keine Gruppe aktiv)"
                : $"LibreHardwareMonitor ({string.Join(", ", groups)})";
        }
    }

    public IReadOnlyList<SensorDescriptor> Descriptors =>
        [CpuTemp, GpuLoad, GpuTemp, GpuFan, GpuVram, GpuPower, GpuHotspot, GpuVramTemp];

    public void Initialize()
    {
        if (!_hardwareConfig.Cpu)
        {
            _log.Info("Die CPU-Gruppe ist per Konfiguration aus (hardware.cpu = false). "
                      + "cpu.temp bleibt damit nicht verfügbar, und es wird kein Kernel-Treiber gebraucht.");
        }
        else if (!PawnIoPresence.IsInstalled)
        {
            _log.Warn("Die CPU-Gruppe ist eingeschaltet, aber PawnIO ist nicht installiert. "
                      + "cpu.temp bleibt nicht verfügbar.");
        }
        else
        {
            _log.Info($"PawnIO gefunden, Version {PawnIoPresence.InstalledVersion}.");
        }

        var computer = new Computer
        {
            IsCpuEnabled = _hardwareConfig.Cpu,
            IsGpuEnabled = _hardwareConfig.Gpu,
            IsStorageEnabled = _hardwareConfig.Storage,

            // Alles Übrige bleibt aus und ist nicht konfigurierbar: Für dieses
            // Projekt gibt es keinen Grund, diese Gruppen anzufassen.
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false
        };

        computer.Open();
        _computer = computer;
        _hardware = [.. computer.Hardware];

        _log.Info($"LibreHardwareMonitor geöffnet, {_hardware.Length} Geräte: "
                  + string.Join(", ", _hardware.Select(h => $"{h.HardwareType} '{h.Name}'")));
    }

    public IReadOnlyList<SensorReading> Read(DateTimeOffset at)
    {
        if (_computer is null)
        {
            return Unavailable(at);
        }

        UpdateHardware();

        if (!_sensorsResolved)
        {
            ResolveSensors();
        }

        return
        [
            ReadTemperature(_cpuTempSensor, CpuTemp, at),
            ReadPlain(_gpuLoadSensor, GpuLoad, at),
            ReadTemperature(_gpuTempSensor, GpuTemp, at),
            ReadPlain(_gpuFanSensor, GpuFan, at),
            ReadPlain(_gpuVramSensor, GpuVram, at),
            ReadPlain(_gpuPowerSensor, GpuPower, at),
            ReadTemperature(_gpuHotspotSensor, GpuHotspot, at),
            ReadTemperature(_gpuVramTempSensor, GpuVramTemp, at)
        ];
    }

    private void UpdateHardware()
    {
        foreach (IHardware hardware in _hardware)
        {
            hardware.Update();

            foreach (IHardware sub in hardware.SubHardware)
            {
                sub.Update();
            }
        }
    }

    /// <summary>
    /// Sucht die Sensoren einmalig heraus. Die Namen werden nicht geraten,
    /// sondern über Vorzugslisten bestimmt und beim Start protokolliert, damit
    /// nachvollziehbar ist, was gewählt wurde.
    /// </summary>
    private void ResolveSensors()
    {
        ISensor[] all = AllSensors().ToArray();

        _cpuTempSensor = Pick(all, HardwareKind.Cpu, SensorType.Temperature,
            ["Tctl/Tdie", "Tdie", "Tctl", "Package", "Core Average", "Core Max"]);

        _gpuLoadSensor = Pick(all, HardwareKind.Gpu, SensorType.Load, ["GPU Core"]);
        _gpuTempSensor = Pick(all, HardwareKind.Gpu, SensorType.Temperature, ["GPU Core", "GPU Hot Spot"]);

        // Der Lüfter soll in Prozent angezeigt werden. SensorType.Fan wäre
        // die Drehzahl, SensorType.Control ist der Prozentwert.
        _gpuFanSensor = Pick(all, HardwareKind.Gpu, SensorType.Control, ["GPU Fan", "Fan"]);

        // Die vier Folgenden werden **streng** gesucht: Passt kein Name, bleibt
        // der Sensor leer. Der Rückfall auf „irgendeinen Sensor dieser Art"
        // wäre hier schädlich - eine Karte ohne Hot-Spot-Fühler würde sonst
        // die Kerntemperatur als Hot Spot ausgeben, und eine ohne eigenen
        // VRAM-Fühler ebenso. Ein fehlender Wert ist ehrlich, ein falscher
        // nicht.
        _gpuVramSensor = Pick(all, HardwareKind.Gpu, SensorType.SmallData,
            ["GPU Memory Used"], strict: true);

        _gpuPowerSensor = Pick(all, HardwareKind.Gpu, SensorType.Power,
            ["GPU Package", "GPU Power"], strict: true);

        _gpuHotspotSensor = Pick(all, HardwareKind.Gpu, SensorType.Temperature,
            ["GPU Hot Spot"], strict: true);

        _gpuVramTempSensor = Pick(all, HardwareKind.Gpu, SensorType.Temperature,
            ["GPU Memory Junction", "GPU Memory"], strict: true);

        if (_hardwareConfig.Cpu)
        {
            LogChoice(CpuTemp, _cpuTempSensor);
        }

        LogChoice(GpuLoad, _gpuLoadSensor);
        LogChoice(GpuTemp, _gpuTempSensor);
        LogChoice(GpuFan, _gpuFanSensor);
        LogChoice(GpuVram, _gpuVramSensor);
        LogChoice(GpuPower, _gpuPowerSensor);
        LogChoice(GpuHotspot, _gpuHotspotSensor);
        LogChoice(GpuVramTemp, _gpuVramTempSensor);

        _sensorsResolved = true;
    }

    private void LogChoice(SensorDescriptor descriptor, ISensor? sensor)
    {
        if (sensor is null)
        {
            // Kein Fehler, sondern eine Eigenschaft der Karte: Nicht jede hat
            // einen Hot-Spot- oder VRAM-Fühler. Der Sensor meldet dann dauerhaft
            // „nicht verfügbar".
            _log.Info($"Für {descriptor.Id} gibt es auf dieser Hardware keinen passenden Sensor.");
            return;
        }

        _log.Info($"{descriptor.Id} liest '{sensor.Hardware.Name} / {sensor.Name}' ({sensor.SensorType}).");
    }

    /// <param name="strict">
    /// Ohne Treffer in <paramref name="preferredNames"/> wird <c>null</c>
    /// zurückgegeben, statt irgendeinen Sensor derselben Art zu nehmen.
    /// </param>
    private static ISensor? Pick(
        ISensor[] all,
        HardwareKind kind,
        SensorType type,
        string[] preferredNames,
        bool strict = false)
    {
        ISensor[] candidates = [.. all.Where(s => s.SensorType == type && KindOf(s.Hardware.HardwareType) == kind)];

        foreach (string name in preferredNames)
        {
            ISensor? exact = candidates.FirstOrDefault(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (string name in preferredNames)
        {
            ISensor? partial = candidates.FirstOrDefault(
                s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }
        }

        return strict ? null : candidates.FirstOrDefault();
    }

    private IEnumerable<ISensor> AllSensors()
    {
        foreach (IHardware hardware in _hardware)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                yield return sensor;
            }

            foreach (IHardware sub in hardware.SubHardware)
            {
                foreach (ISensor sensor in sub.Sensors)
                {
                    yield return sensor;
                }
            }
        }
    }

    private static SensorReading ReadPlain(ISensor? sensor, SensorDescriptor descriptor, DateTimeOffset at)
        => sensor?.Value is { } value && !float.IsNaN(value)
            ? SensorReading.Ok(descriptor, value, at)
            : SensorReading.Unavailable(descriptor, at);

    /// <summary>
    /// Wie <see cref="ReadPlain"/>, zusätzlich mit der Plausibilitätsgrenze aus
    /// Entscheidung 0007: Eine gemeldete 0 °C ist ein Fehler, kein Messwert.
    /// </summary>
    private static SensorReading ReadTemperature(ISensor? sensor, SensorDescriptor descriptor, DateTimeOffset at)
        => sensor?.Value is { } value && !float.IsNaN(value) && value >= MinimumPlausibleTemperature
            ? SensorReading.Ok(descriptor, value, at)
            : SensorReading.Unavailable(descriptor, at);

    private static SensorReading[] Unavailable(DateTimeOffset at)
    =>
    [
        SensorReading.Unavailable(CpuTemp, at),
        SensorReading.Unavailable(GpuLoad, at),
        SensorReading.Unavailable(GpuTemp, at),
        SensorReading.Unavailable(GpuFan, at),
        SensorReading.Unavailable(GpuVram, at),
        SensorReading.Unavailable(GpuPower, at),
        SensorReading.Unavailable(GpuHotspot, at),
        SensorReading.Unavailable(GpuVramTemp, at)
    ];

    /// <summary>
    /// Listet alle gefundenen Sensoren auf, für den Modus <c>--sensors</c>.
    /// Reine Diagnose, damit Sensornamen nicht geraten werden müssen.
    /// </summary>
    public IEnumerable<string> DescribeAllSensors()
    {
        if (_computer is null)
        {
            yield return "LibreHardwareMonitor ist nicht geöffnet.";
            yield break;
        }

        UpdateHardware();

        foreach (IHardware hardware in _hardware)
        {
            yield return $"{hardware.HardwareType}  {hardware.Name}";

            foreach (ISensor sensor in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
            {
                yield return FormatSensor(sensor);
            }

            foreach (IHardware sub in hardware.SubHardware)
            {
                yield return $"  [Unterhardware] {sub.HardwareType}  {sub.Name}";

                foreach (ISensor sensor in sub.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
                {
                    yield return "  " + FormatSensor(sensor);
                }
            }
        }
    }

    private static string FormatSensor(ISensor sensor)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "    {0,-14} {1,-28} {2}",
            sensor.SensorType,
            sensor.Name,
            sensor.Value.HasValue ? sensor.Value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "--");

    private enum HardwareKind
    {
        Cpu,
        Gpu,
        Other
    }

    private static HardwareKind KindOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        _ => HardwareKind.Other
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _computer?.Close();
        }
        catch (Exception error)
        {
            _log.Warn("LibreHardwareMonitor ließ sich nicht sauber schließen.", error);
        }

        _computer = null;
        _hardware = [];
    }
}
