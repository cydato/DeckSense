using DeckSense.Service.Api;
using DeckSense.Service.Configuration;
using DeckSense.Service.Interop;
using DeckSense.Service.Logging;
using DeckSense.Service.Runtime;
using DeckSense.Service.Sensors;
using DeckSense.Service.Sensors.Sources;

namespace DeckSense.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        bool hilfe = args.Contains("--help") || args.Contains("-h") || args.Contains("/?");
        bool diagnostics = args.Contains("--diag");
        bool once = args.Contains("--once");
        bool dumpSensors = args.Contains("--sensors");
        bool detectDrives = args.Contains("--detect");

        // Als WinExe gibt es beim Start aus einer Eingabeaufforderung keine
        // eigene Konsole. Für die Diagnosemodi wird die des Aufrufers
        // übernommen - vor jeder Ausgabe, sonst zeigen die Streams schon ins
        // Leere. Siehe Entscheidung 0027.
        if (hilfe || diagnostics || once || dumpSensors || detectDrives)
        {
            ParentConsole.Anhaengen();
        }

        TrySetUtf8Console();

        if (hilfe)
        {
            PrintHelp();
            return 0;
        }

        // Nicht zwingend der Programmordner: Liegt das Programm unter
        // „Program Files", wandern Konfiguration und Protokoll nach
        // %ProgramData%. Siehe AppPaths.
        string baseDirectory = AppPaths.DatenVerzeichnis();

        // --detect und --sensors sind rein lesend und sollen nichts
        // hinterlassen. Ohne diesen Schalter legten sie eine appsettings.json
        // samt Seriennummern an, auch im Veröffentlichungsordner.
        bool nurLesen = detectDrives || dumpSensors;
        AppConfig config = AppConfig.Load(baseDirectory, out List<string> configNotes, darfErzeugen: !nurLesen);

        string logPath = Path.IsPathRooted(config.Logging.Path)
            ? config.Logging.Path
            : Path.Combine(baseDirectory, config.Logging.Path);

        using var log = new FileLog(
            logPath,
            config.Logging.Level,
            config.Logging.MaxFileSizeKb,
            config.Logging.MaxFiles,
            mirrorToConsole: diagnostics || once || dumpSensors || detectDrives);

        log.Info($"{AppInfo.Name} {AppInfo.Version} startet. Takt {config.Intervals.FastMs} ms.");

        if (AppPaths.NutztProgramData)
        {
            log.Info($"{AppPaths.Grund}. Konfiguration und Protokoll liegen unter {baseDirectory}.");
        }
        foreach (string note in configNotes)
        {
            log.Warn(note);
        }

        if (detectDrives)
        {
            return RunDriveDetection(log);
        }

        if (dumpSensors)
        {
            return RunSensorDump(log);
        }


        var store = new SnapshotStore();

        List<ISensorSource> fastSources = Initialize(log,
        [
            new CpuCounterSource(log),
            new MemorySource(),
            new RtssSource(log),
            new LhmSource(log, config.Hardware)
        ]);

        // Eigener Takt: Laufwerke werden deutlich seltener gelesen, und ihre
        // Abfrage darf den schnellen Takt nicht aufhalten.
        List<ISensorSource> storageSources = Initialize(log, [new StorageSource(log, config.Storage)]);

        if (fastSources.Count == 0 && storageSources.Count == 0)
        {
            log.Error("Keine einzige Sensorquelle ist startbar. Der Dienst endet.");
            return 2;
        }

        using var fastSampler = new SensorSampler(
            "schnell", fastSources, store, log, TimeSpan.FromMilliseconds(config.Intervals.FastMs));

        // Die Laufwerke messen sofort mit: Eine Temperatur ist ein
        // Momentanwert, und 15 Sekunden „--" nach jedem Dienststart wären
        // unnötig.
        using var storageSampler = new SensorSampler(
            "Laufwerke", storageSources, store, log,
            TimeSpan.FromMilliseconds(config.Intervals.StorageMs), sampleImmediately: true);

        using var api = new HttpApi(store, log, config.Http.Port);

        if (once)
        {
            return await RunOnceAsync(fastSampler, storageSampler, store, config).ConfigureAwait(false);
        }

        fastSampler.Start();
        storageSampler.Start();
        api.Start();

        // Erst hinter dem Abzweig für --once: Ein einzelner Messdurchgang
        // braucht keinen Aufräumtakt. Siehe Entscheidung 0022.
        using var heapTrim = new HeapTrim(log);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        if (diagnostics)
        {
            Console.WriteLine($"{AppInfo.Name} {AppInfo.Version} - Diagnosemodus, Abbruch mit Strg+C");
            await RunDiagnosticsLoopAsync(store, config, stopping.Token).ConfigureAwait(false);
        }
        else
        {
            await WaitForShutdownAsync(stopping.Token).ConfigureAwait(false);
        }

        log.Info($"Dienst endet. Übersprungene Takte: schnell {fastSampler.SkippedTicks}, "
                 + $"Laufwerke {storageSampler.SkippedTicks}.");
        return 0;
    }

    /// <summary>
    /// Startet die Quellen einzeln. Eine Quelle, die nicht startet, darf den
    /// Dienst nicht hindern.
    /// </summary>
    private static List<ISensorSource> Initialize(ILog log, List<ISensorSource> sources)
    {
        var usable = new List<ISensorSource>(sources.Count);

        foreach (ISensorSource source in sources)
        {
            try
            {
                source.Initialize();

                if (source.Descriptors.Count == 0)
                {
                    log.Info($"Quelle '{source.Name}' liefert keine Sensoren und bleibt aus.");
                    source.Dispose();
                    continue;
                }

                usable.Add(source);
                log.Info($"Quelle bereit: {source.Name}");
            }
            catch (Exception error)
            {
                log.Error($"Quelle '{source.Name}' konnte nicht starten und bleibt aus.", error);
                source.Dispose();
            }
        }

        return usable;
    }

    /// <summary>
    /// Einmal messen und ausgeben. Die CPU-Indikatoren brauchen zwei
    /// Messpunkte, deshalb wird ein Takt abgewartet, bevor gelesen wird.
    /// </summary>
    private static async Task<int> RunOnceAsync(
        SensorSampler fastSampler,
        SensorSampler storageSampler,
        SnapshotStore store,
        AppConfig config)
    {
        await Task.Delay(config.Intervals.FastMs).ConfigureAwait(false);
        fastSampler.SampleOnce();
        storageSampler.SampleOnce();

        SensorSnapshot snapshot = store.Current;
        Console.WriteLine();
        Console.WriteLine($"As of {snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}");
        Console.WriteLine();
        Console.WriteLine("{0,-30} {1,-20} {2,12}", "ID", "Name", "Value");
        Console.WriteLine(new string('-', 64));

        foreach (SensorReading reading in snapshot.Readings)
        {
            Console.WriteLine("{0,-30} {1,-20} {2,12}",
                reading.Descriptor.Id,
                reading.Descriptor.Name,
                reading.Format());
        }

        Console.WriteLine();
        return snapshot.Readings.Any(r => r.Available) ? 0 : 3;
    }

    /// <summary>
    /// Listet die erkannten Laufwerke samt der Sensor-Id auf, die DeckSense
    /// ihnen geben würde. Liest nur Geräteeigenschaften, keine SMART-Daten, und
    /// ändert nichts an der Konfiguration.
    /// </summary>
    private static int RunDriveDetection(ILog log)
    {
        IReadOnlyList<StorageDeviceInfo> drives;

        try
        {
            drives = StorageDetector.Enumerate();
        }
        catch (Exception error)
        {
            log.Error("Die Laufwerke konnten nicht erkannt werden. Ohne Admin-Rechte ist das zu erwarten.", error);
            return 5;
        }

        Console.WriteLine();
        Console.WriteLine($"{drives.Count} drives detected. Only device properties were read.");
        Console.WriteLine();
        Console.WriteLine("{0,-4} {1,-26} {2,-5} {3,-6} {4,8}  {5}", "No.", "Model", "Type", "Bus", "GB", "Sensor ID");
        Console.WriteLine(new string('-', 100));

        foreach (StorageDeviceInfo drive in drives)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"{drive.DeviceNumber,-4} {Shorten(drive.FriendlyName, 26),-26} {drive.MediaType,-5} {drive.BusType,-6} {drive.SizeGigabytes,8:N0}  {(drive.LooksLikeFixedDisk ? ConfigBootstrap.BuildId(drive) : " - removable, not enabled")}"));
        }

        Console.WriteLine();
        Console.WriteLine("Drives are matched by serial number, not by drive number.");
        Console.WriteLine();
        return 0;
    }

    private static string Shorten(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    /// <summary>
    /// Listet alles auf, was LibreHardwareMonitor in den freigegebenen Gruppen
    /// findet. Dient dazu, die tatsächlichen Sensornamen dieser Maschine zu
    /// sehen, statt sie zu raten.
    /// </summary>
    private static int RunSensorDump(ILog log)
    {
        // Für die Diagnose werden CPU und GPU eingeschaltet, unabhängig von der
        // Konfiguration - man will ja sehen, was es gäbe. Storage bleibt aus:
        // Die ausgeschlossene Intel-NVMe wird auch für einen Dump nicht
        // angefasst.
        using var source = new LhmSource(log, new HardwareConfig { Cpu = true, Gpu = true });

        try
        {
            source.Initialize();
        }
        catch (Exception error)
        {
            log.Error("LibreHardwareMonitor konnte nicht geöffnet werden.", error);
            return 4;
        }

        Console.WriteLine();
        Console.WriteLine("Sensors found (CPU and GPU groups only):");
        Console.WriteLine();

        foreach (string line in source.DescribeAllSensors())
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        return 0;
    }

    private static async Task RunDiagnosticsLoopAsync(SnapshotStore store, AppConfig config, CancellationToken token)
    {
        DateTimeOffset lastShown = DateTimeOffset.MinValue;

        try
        {
            while (!token.IsCancellationRequested)
            {
                SensorSnapshot snapshot = store.Current;

                // Nur ausgeben, wenn wirklich ein neuer Stand vorliegt. Sonst
                // wiederholt die Anzeige denselben Takt, weil Ausgabeschleife
                // und Abfragetakt nicht gekoppelt sind.
                if (snapshot.GeneratedAt > lastShown)
                {
                    lastShown = snapshot.GeneratedAt;
                    string values = string.Join("   ", snapshot.Readings.Select(r => $"{r.Descriptor.Id} {r.Format()}"));
                    Console.WriteLine($"{snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}  {values}");
                }

                await Task.Delay(config.Intervals.FastMs / 4, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Strg+C, regulärer Weg nach draußen.
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Strg+C, regulärer Weg nach draußen.
        }
    }

    /// <summary>
    /// Ohne das schreibt die Konsole Umlaute und Gedankenstriche in der
    /// OEM-Codepage und damit verstümmelt, sobald die Ausgabe umgeleitet wird.
    /// </summary>
    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception)
        {
            // Nicht jeder Ausgabekanal lässt das zu. Dann bleibt es, wie es ist.
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            {AppInfo.Name} {AppInfo.Version}

            Without arguments: runs silently and without a window, logs to the
            file named in appsettings.json, keeps the values cached and answers
            requests on http://127.0.0.1:5891/api/sensors.

              --once     take one reading, print a table, exit
              --diag     keep running and print one line per tick
              --detect   list the detected drives with their sensor IDs and
                         exit; changes nothing, writes nothing
              --sensors  list every sensor LibreHardwareMonitor can see and
                         exit
              --help     this help

            The log file stays German. Messages on screen are English.
            """);
    }
}
