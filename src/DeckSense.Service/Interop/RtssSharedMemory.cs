using System.IO.MemoryMappedFiles;
using System.Text;

namespace DeckSense.Service.Interop;

/// <summary>
/// Lesender Zugriff auf das Shared Memory von RivaTuner Statistics Server.
///
/// Die Feldversätze stammen aus <c>RTSSSharedMemory.h</c> aus der
/// RTSS-Installation (SDK/Include), nicht aus dem Gedächtnis. Die Einträge im
/// Anwendungsarray werden über <c>dwAppArrOffset</c> und
/// <c>dwAppEntrySize</c> adressiert - der Header warnt ausdrücklich davor, die
/// Arrays direkt zu adressieren, damit künftige Strukturversionen nicht
/// brechen. Innerhalb eines Eintrags werden nur die vordersten Felder
/// gelesen; die stehen seit Strukturversion 1.0 unverändert am Anfang.
/// </summary>
internal sealed class RtssSharedMemory : IDisposable
{
    /// <summary>Name des Mapping-Objekts, siehe RTSS-SDK.</summary>
    private const string MapName = "RTSSSharedMemoryV2";

    /// <summary>'RTSS' als DWORD gelesen.</summary>
    private const uint Signature = 0x52545353;

    /// <summary>Mindestens Strukturversion 2.0.</summary>
    private const uint MinimumVersion = 0x0002_0000;

    /// <summary>Ab 2.16 gibt es dwLastForegroundApp und die zugehörige PID.</summary>
    private const uint ForegroundFieldsVersion = 0x0002_0010;

    // Versätze im Kopf der Struktur.
    private const long OffsetSignature = 0;
    private const long OffsetVersion = 4;
    private const long OffsetAppEntrySize = 8;
    private const long OffsetAppArrOffset = 12;
    private const long OffsetAppArrSize = 16;
    private const long OffsetLastForegroundAppProcessId = 68;

    // Versätze innerhalb eines Eintrags im Anwendungsarray.
    private const long EntryProcessId = 0;
    private const long EntryName = 4;
    private const int EntryNameLength = 260; // MAX_PATH
    private const long EntryTime0 = 268;
    private const long EntryTime1 = 272;
    private const long EntryFrames = 276;
    private const long EntryFrameTime = 280;

    /// <summary>
    /// Ab dieser Anzahl Millisekunden ohne neues Bild gilt ein Eintrag als
    /// veraltet. 2000 ms ist der Wert, den auch die RTSS-eigenen Plugins
    /// verwenden.
    /// </summary>
    private const uint MaxAgeMs = 2000;

    /// <summary>
    /// Kürzere Messperioden werden verworfen: Der Quotient aus Bildern und
    /// Zeitspanne wäre dann nicht belastbar.
    /// </summary>
    private const uint MinimumPeriodMs = 100;

    /// <summary>
    /// Längere Messperioden werden ebenfalls verworfen.
    ///
    /// RTSS beginnt eine neue Periode, wenn ein Bild ausgegeben wird, höchstens
    /// aber einmal pro Sekunde. Üblich sind daher etwa 1000 bis 1300 ms. Eine
    /// deutlich längere Periode bedeutet, dass die Anwendung dazwischen
    /// praktisch nichts ausgegeben hat - gemessen wurde dann ein einzelnes Bild
    /// über eine Pause hinweg. Belegt im Test: <c>dwFrames=1</c> bei einer
    /// Periode von 7281 ms, was 0,1 FPS ergab, obwohl das Spiel unmittelbar
    /// danach wieder 143 FPS lieferte.
    /// </summary>
    private const uint MaximumPeriodMs = 2500;

    /// <summary>
    /// So oft wird versucht, die drei Felder der Messperiode in einem
    /// stabilen Zustand zu lesen.
    /// </summary>
    private const int PeriodReadAttempts = 4;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private RtssSharedMemory(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
    }

    /// <summary>
    /// Versucht, das Shared Memory zu öffnen. Gibt <c>null</c> zurück, wenn
    /// RTSS nicht läuft - das ist der Normalfall ohne Spiel und kein Fehler.
    /// </summary>
    internal static RtssSharedMemory? TryOpen()
    {
        MemoryMappedFile? file = TryOpenMapping();
        if (file is null)
        {
            return null;
        }

        try
        {
            MemoryMappedViewAccessor view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new RtssSharedMemory(file, view);
        }
        catch (Exception)
        {
            file.Dispose();
            return null;
        }
    }

    private static MemoryMappedFile? TryOpenMapping()
    {
        try
        {
            return MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            // RTSS läuft nicht.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Manche RTSS-Fassungen geben das Mapping nur mit Vollzugriff her.
            try
            {
                return MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            }
            catch (Exception)
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Prüft Signatur und Version. Schlägt das fehl, ist das Mapping
    /// unbrauchbar und sollte neu geöffnet werden.
    /// </summary>
    internal bool IsValid()
    {
        try
        {
            return _view.ReadUInt32(OffsetSignature) == Signature
                   && _view.ReadUInt32(OffsetVersion) >= MinimumVersion;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sucht den Eintrag der Anwendung im Vordergrund und liefert dessen
    /// Bildraten. <c>null</c>, wenn nichts Passendes mit frischen Daten
    /// gefunden wurde.
    /// </summary>
    /// <param name="foregroundProcessId">
    /// PID des Vordergrundfensters. 0, wenn unbekannt.
    /// </param>
    internal RtssFrameInfo? ReadForeground(uint foregroundProcessId)
    {
        uint version = _view.ReadUInt32(OffsetVersion);
        uint entrySize = _view.ReadUInt32(OffsetAppEntrySize);
        uint arrayOffset = _view.ReadUInt32(OffsetAppArrOffset);
        uint arrayCount = _view.ReadUInt32(OffsetAppArrSize);

        if (entrySize == 0 || arrayCount == 0)
        {
            return null;
        }

        // Erste Wahl: der Prozess, dessen Fenster gerade im Vordergrund ist.
        if (foregroundProcessId != 0)
        {
            RtssFrameInfo? match = FindByProcessId(foregroundProcessId, arrayOffset, entrySize, arrayCount);
            if (match is not null)
            {
                return match;
            }
        }

        // Zweite Wahl: der Eintrag, den RTSS selbst als letzte
        // Vordergrundanwendung führt. Hilft, wenn das Spiel gerade keinen
        // Fokus hat, aber weiter rendert.
        if (version >= ForegroundFieldsVersion)
        {
            uint lastForeground = _view.ReadUInt32(OffsetLastForegroundAppProcessId);
            if (lastForeground != 0 && lastForeground != foregroundProcessId)
            {
                return FindByProcessId(lastForeground, arrayOffset, entrySize, arrayCount);
            }
        }

        return null;
    }

    private RtssFrameInfo? FindByProcessId(uint processId, uint arrayOffset, uint entrySize, uint arrayCount)
    {
        for (uint index = 0; index < arrayCount; index++)
        {
            long entry = arrayOffset + (long)index * entrySize;

            if (_view.ReadUInt32(entry + EntryProcessId) != processId)
            {
                continue;
            }

            return ReadEntry(entry);
        }

        return null;
    }

    private RtssFrameInfo? ReadEntry(long entry)
    {
        if (!TryReadPeriod(entry, out uint time0, out uint time1, out uint frames))
        {
            return null;
        }

        uint frameTimeMicroseconds = _view.ReadUInt32(entry + EntryFrameTime);

        // Der Header ist hier eindeutig: dwTime0 muss ungleich 0 sein, damit
        // sich die Bildrate überhaupt berechnen lässt.
        if (time0 == 0 || time1 == 0 || time1 <= time0)
        {
            return null;
        }

        // Rendert die Anwendung noch? Beide Zeitstempel sind Millisekunden aus
        // GetTickCount, deshalb dieselbe Basis und derselbe Überlauf.
        uint now = unchecked((uint)Environment.TickCount);
        uint age = unchecked(now - time1);
        if (age > MaxAgeMs)
        {
            return null;
        }

        // Die Bildzeit ist vom Zustand der Messperiode unabhängig: Sie gilt für
        // das zuletzt ausgegebene Bild. Sie bleibt daher auch dann gültig, wenn
        // die Periode unbrauchbar ist.
        uint period = time1 - time0;
        bool periodUsable = period >= MinimumPeriodMs && period <= MaximumPeriodMs;

        double? framesPerSecond = frames == 0 || !periodUsable
            ? null
            : 1000.0 * frames / period;

        // RTSS führt die Bildzeit in Mikrosekunden.
        double? frameTimeMs = frameTimeMicroseconds == 0
            ? null
            : frameTimeMicroseconds / 1000.0;

        if (framesPerSecond is null && frameTimeMs is null)
        {
            return null;
        }

        return new RtssFrameInfo(
            ReadName(entry),
            framesPerSecond,
            frameTimeMs,
            frames,
            period,
            frameTimeMicroseconds);
    }

    /// <summary>
    /// Liest <c>dwTime0</c>, <c>dwTime1</c> und <c>dwFrames</c> so, dass die
    /// drei Werte zur selben Messperiode gehören.
    ///
    /// RTSS tauscht die drei Felder etwa einmal pro Sekunde aus, und es gibt
    /// keine Sperre, die einen Leser dabei aussperrt. Wer sie einfach
    /// nacheinander liest, kann eine Mischung aus alter und neuer Periode
    /// erwischen - etwa die Bildanzahl der neuen und die Zeitspanne der
    /// alten. Der Quotient ist dann zu niedrig, ohne dass irgendetwas
    /// offensichtlich falsch aussieht.
    ///
    /// Deshalb wird doppelt gelesen und nur akzeptiert, was zweimal
    /// übereinstimmt. Da RTSS nur einmal pro Sekunde schreibt, ist der erste
    /// Versuch praktisch immer schon stabil.
    /// </summary>
    private bool TryReadPeriod(long entry, out uint time0, out uint time1, out uint frames)
    {
        for (int attempt = 0; attempt < PeriodReadAttempts; attempt++)
        {
            time0 = _view.ReadUInt32(entry + EntryTime0);
            time1 = _view.ReadUInt32(entry + EntryTime1);
            frames = _view.ReadUInt32(entry + EntryFrames);

            if (_view.ReadUInt32(entry + EntryTime0) == time0
                && _view.ReadUInt32(entry + EntryTime1) == time1
                && _view.ReadUInt32(entry + EntryFrames) == frames)
            {
                return true;
            }
        }

        // Vier Versuche ohne stabiles Ergebnis: Dann lieber nichts melden als
        // eine Zahl, die aus zwei Perioden zusammengesetzt ist.
        time0 = 0;
        time1 = 0;
        frames = 0;
        return false;
    }

    private string ReadName(long entry)
    {
        var buffer = new byte[EntryNameLength];
        _view.ReadArray(entry + EntryName, buffer, 0, buffer.Length);

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        string path = Encoding.Latin1.GetString(buffer, 0, length);
        return path.Length == 0 ? "?" : Path.GetFileName(path);
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// Was RTSS über die Anwendung im Vordergrund hergibt. Einzelne Werte können
/// fehlen, auch wenn der Eintrag gültig ist.
/// </summary>
/// <param name="ProcessName">Dateiname der Exe, nur für das Protokoll.</param>
/// <param name="Frames">Rohwert <c>dwFrames</c>, für die Fehlersuche.</param>
/// <param name="PeriodMs">Rohwert <c>dwTime1 - dwTime0</c>, für die Fehlersuche.</param>
/// <param name="FrameTimeMicroseconds">Rohwert <c>dwFrameTime</c>, für die Fehlersuche.</param>
internal sealed record RtssFrameInfo(
    string ProcessName,
    double? FramesPerSecond,
    double? FrameTimeMs,
    uint Frames,
    uint PeriodMs,
    uint FrameTimeMicroseconds);
