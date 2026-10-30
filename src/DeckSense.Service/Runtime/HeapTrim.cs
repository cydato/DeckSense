using System.Runtime;
using DeckSense.Service.Logging;

namespace DeckSense.Service.Runtime;

/// <summary>
/// Räumt die Halde in festem Takt auf.
///
/// <para>
/// <b>Warum das überhaupt nötig ist.</b> Gemessen am laufenden Dienst: rund
/// 20 KB/s Zuteilungen - etwa 5 MB je fünf Minuten - und in jeder Messung
/// <b>keine einzige Sammlung</b>, bei Laufzeiten von 8 bis 17 Minuten. Das
/// Budget für gen0 richtet sich nach der Maschine, und auf
/// einem Rechner mit viel Arbeitsspeicher ist es groß genug, dass ein Dienst
/// dieser Größe stundenlang alles behält, was er je zugeteilt hat. Der Working
/// Set folgt dann einfach der Summe aller Zuteilungen - in 17 Minuten von
/// 110 auf 126 MB, ohne Leck, nur weil nichts eingesammelt wurde.
/// </para>
///
/// <para>
/// Für ein Programm, das Minuten läuft, ist das die richtige Vorgabe: Nicht
/// aufzuräumen ist schneller. DeckSense soll aber <b>tagelang</b> laufen - der
/// Zwölf-Stunden-Riegel von HWiNFO war der Anlass für dieses Projekt. Ein
/// Verbrauch, der mit der Laufzeit wächst, wäre derselbe Fehler in anderer
/// Form.
/// </para>
///
/// <para>
/// Die Sammlung kostet Rechenzeit in unserem eigenen Prozess und hält ihn
/// kurz an. Das ist unbedenklich: Die oberste Regel des Projekts richtet sich
/// gegen Latenz im <b>Kernel</b> - DPC-Spitzen durch Sensorabfragen. Eine
/// GC-Pause im Benutzermodus blockiert keinen Interrupt und verursacht kein
/// Audio-Knacken. Siehe Entscheidung 0022.
/// </para>
/// </summary>
internal sealed class HeapTrim : IDisposable
{
    /// <summary>
    /// Fünf Minuten. Gemessen fallen je Durchgang etwa 5 MB an, von denen
    /// rund 2 MB Halde stehen bleiben - klein genug, dass die Sammlung unter
    /// einer Millisekunde bleibt, und oft genug, dass der Verbrauch nicht
    /// wegläuft.
    ///
    /// <para>
    /// Absichtlich <b>nicht</b> einstellbar: Der Wert betrifft keine Hardware,
    /// und es gibt nichts zu gewinnen, wenn daran gedreht wird.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Intervall = TimeSpan.FromMinutes(5);

    private readonly ILog _log;
    private readonly Timer _timer;

    /// <summary>Stand der Gesamtzuteilungen beim letzten Durchgang.</summary>
    private long _zugeteiltBisher;

    private bool _disposed;

    internal HeapTrim(ILog log)
    {
        _log = log;
        _zugeteiltBisher = GC.GetTotalAllocatedBytes(precise: false);
        _timer = new Timer(Aufraeumen, null, Intervall, Intervall);
    }

    private void Aufraeumen(object? zustand)
    {
        try
        {
            long beginn = Environment.TickCount64;

            // Das von Microsoft dokumentierte Rezept, um die Halde auf die
            // kleinstmögliche Größe zu bringen: große Objekte einmalig
            // mitverdichten, dann eine blockierende, verdichtende Sammlung
            // über alle Generationen.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            long dauer = Environment.TickCount64 - beginn;

            // Protokolliert werden die Haldengröße **nach** der Sammlung und
            // die Zuteilungen seit dem letzten Durchgang.
            //
            // Nicht etwa „vorher auf nachher": Für das Vorher gibt es nur
            // GetTotalMemory, und das ist eine andere Messgröße als
            // GetGCMemoryInfo - die Schätzung liefert direkt nach einer
            // verdichtenden Sammlung sogar kleine negative Werte, weil der
            // Zuteilungspuffer des Threads gegengerechnet wird. Erst stand im
            // Protokoll „auf -261 KB", dann „1627 KB auf 1814 KB", als hätte
            // das Aufräumen geschadet. Zwei Zahlen derselben Art sind
            // aussagekräftiger als ein Vergleich zweier verschiedener.
            long zugeteilt = GC.GetTotalAllocatedBytes(precise: false);
            long seitdem = zugeteilt - _zugeteiltBisher;
            _zugeteiltBisher = zugeteilt;

            long halde = GC.GetGCMemoryInfo().HeapSizeBytes;

            // Nur Debug: Im Betrieb soll das Protokoll schlank bleiben, und
            // alle fünf Minuten eine Zeile wäre für eine Selbstverständlichkeit
            // zu viel.
            _log.Debug($"Halde aufgeräumt in {dauer} ms: {halde / 1024} KB Halde, "
                       + $"{seitdem / 1024} KB zugeteilt seit dem letzten Durchgang");
        }
        catch (Exception error)
        {
            // Darf den Dienst nie beenden. Wenn das Aufräumen scheitert,
            // läuft er mit mehr Speicher weiter - das ist das kleinere Übel.
            _log.Warn("Das Aufräumen der Halde ist fehlgeschlagen.", error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}
