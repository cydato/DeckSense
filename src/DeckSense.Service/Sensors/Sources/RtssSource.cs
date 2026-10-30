using DeckSense.Service.Interop;
using DeckSense.Service.Logging;

namespace DeckSense.Service.Sensors.Sources;

/// <summary>
/// FPS und Bildzeit der Anwendung im Vordergrund, aus dem RTSS-Shared-Memory.
///
/// Ohne laufendes RTSS oder ohne Spiel im Vordergrund melden beide Sensoren
/// <c>available: false</c> - ausdrücklich <b>nicht</b> 0. Eine Bildrate von 0
/// wäre eine Aussage über das Spiel; „nicht verfügbar" ist die Wahrheit.
/// </summary>
public sealed class RtssSource : ISensorSource
{
    public static readonly SensorDescriptor Fps =
        new("rtss.fps", "FPS", SensorCategory.Rtss, "FPS");

    public static readonly SensorDescriptor FrameTime =
        new("rtss.frametime", "Frametime", SensorCategory.Rtss, "ms");

    /// <summary>
    /// So lange wird nach einem Fehlschlag nicht erneut versucht, das Mapping
    /// zu öffnen. RTSS startet oft erst später als dieser Dienst.
    /// </summary>
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(5);

    private readonly ILog _log;

    private RtssSharedMemory? _memory;
    private DateTimeOffset _nextOpenAttempt = DateTimeOffset.MinValue;
    private bool _openLogged;
    private string? _lastProcessName;
    private bool _disposed;

    public RtssSource(ILog log) => _log = log;

    public string Name => "RTSS Shared Memory";

    public IReadOnlyList<SensorDescriptor> Descriptors => [Fps, FrameTime];

    public void Initialize()
    {
        // Bewusst ohne Ausnahme: Dass RTSS beim Start des Dienstes noch nicht
        // läuft, ist der Normalfall und kein Grund, die Quelle abzuschalten.
        if (!TryEnsureOpen())
        {
            _log.Info("RTSS ist derzeit nicht erreichbar. FPS und Frametime bleiben bis dahin nicht verfügbar.");
        }
    }

    public IReadOnlyList<SensorReading> Read(DateTimeOffset at)
    {
        if (!TryEnsureOpen())
        {
            return Unavailable(at);
        }

        RtssFrameInfo? info;
        try
        {
            info = _memory!.ReadForeground(ForegroundWindow.GetProcessId());
        }
        catch (Exception error)
        {
            // Mapping ist unter uns weggebrochen, etwa weil RTSS beendet wurde.
            _log.Warn("Lesen aus dem RTSS-Shared-Memory ist fehlgeschlagen. Das Mapping wird neu geöffnet.", error);
            CloseMemory();
            return Unavailable(at);
        }

        if (info is null)
        {
            if (_lastProcessName is not null)
            {
                _log.Info($"Keine rendernde Anwendung mehr im Vordergrund (zuletzt {_lastProcessName}).");
                _lastProcessName = null;
            }

            return Unavailable(at);
        }

        if (info.ProcessName != _lastProcessName)
        {
            _log.Info($"RTSS liefert jetzt Werte für {info.ProcessName}.");
            _lastProcessName = info.ProcessName;
        }

        // Rohwerte auf Debug-Ebene, damit sich Unstimmigkeiten zwischen FPS
        // und Bildzeit nachvollziehen lassen. Im Betrieb ist die Ebene
        // Information, das kostet also nichts.
        _log.Debug(FormattableString.Invariant(
            $"RTSS {info.ProcessName}: dwFrames={info.Frames} Periode={info.PeriodMs} ms dwFrameTime={info.FrameTimeMicroseconds} us -> {info.FramesPerSecond:0.0} FPS, {info.FrameTimeMs:0.00} ms"));

        return
        [
            info.FramesPerSecond is { } fps
                ? SensorReading.Ok(Fps, fps, at)
                : SensorReading.Unavailable(Fps, at),
            info.FrameTimeMs is { } frameTime
                ? SensorReading.Ok(FrameTime, frameTime, at)
                : SensorReading.Unavailable(FrameTime, at)
        ];
    }

    private static SensorReading[] Unavailable(DateTimeOffset at)
        => [SensorReading.Unavailable(Fps, at), SensorReading.Unavailable(FrameTime, at)];

    /// <summary>
    /// Stellt sicher, dass ein gültiges Mapping offen ist. Schlägt das fehl,
    /// wird erst nach <see cref="ReopenDelay"/> erneut versucht.
    /// </summary>
    private bool TryEnsureOpen()
    {
        if (_memory is not null)
        {
            if (_memory.IsValid())
            {
                return true;
            }

            // Signatur stimmt nicht mehr: RTSS hat das Mapping zur Freigabe
            // markiert oder neu angelegt.
            CloseMemory();
        }

        if (DateTimeOffset.UtcNow < _nextOpenAttempt)
        {
            return false;
        }

        _nextOpenAttempt = DateTimeOffset.UtcNow + ReopenDelay;
        RtssSharedMemory? opened = RtssSharedMemory.TryOpen();

        if (opened is null)
        {
            return false;
        }

        if (!opened.IsValid())
        {
            opened.Dispose();
            return false;
        }

        _memory = opened;

        if (!_openLogged)
        {
            _log.Info("RTSS-Shared-Memory geöffnet.");
            _openLogged = true;
        }

        return true;
    }

    private void CloseMemory()
    {
        _memory?.Dispose();
        _memory = null;
        _openLogged = false;
        _lastProcessName = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseMemory();
    }
}
