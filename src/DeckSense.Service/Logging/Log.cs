using System.Text;

namespace DeckSense.Service.Logging;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface ILog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message, Exception? error = null);

    void Error(string message, Exception? error = null);
}

/// <summary>
/// Schlankes Protokoll in eine rotierende Datei. Keine Konsolenausgabe im
/// Betrieb - die gibt es nur im Diagnosemodus.
///
/// Grundsatz: Protokollieren darf den Dienst nie zum Absturz bringen. Alle
/// Dateizugriffe sind daher eingefasst und schlucken ihre Fehler.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _minimum;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly bool _mirrorToConsole;
    private bool _writeFailed;

    public FileLog(string path, LogLevel minimum, int maxFileSizeKb, int maxFiles, bool mirrorToConsole)
    {
        _path = Path.GetFullPath(path);
        _minimum = minimum;
        _maxBytes = Math.Max(64, maxFileSizeKb) * 1024L;
        _maxFiles = Math.Clamp(maxFiles, 1, 20);
        _mirrorToConsole = mirrorToConsole;

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception)
        {
            _writeFailed = true;
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Information, message, null);

    public void Warn(string message, Exception? error = null) => Write(LogLevel.Warning, message, error);

    public void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

    private void Write(LogLevel level, string message, Exception? error)
    {
        if (level < _minimum)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append("  ")
            .Append(Tag(level))
            .Append("  ")
            .Append(message);

        if (error is not null)
        {
            line.Append("  | ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        }

        string text = line.ToString();

        if (_mirrorToConsole)
        {
            Console.Error.WriteLine(text);
        }

        lock (_gate)
        {
            if (_writeFailed)
            {
                return;
            }

            try
            {
                RotateIfNeeded();
                File.AppendAllText(_path, text + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Ein nicht schreibbares Protokoll darf den Dienst nicht beenden.
                _writeFailed = true;
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        string oldest = _path + "." + _maxFiles;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _maxFiles - 1; index >= 1; index--)
        {
            string from = _path + "." + index;
            if (File.Exists(from))
            {
                File.Move(from, _path + "." + (index + 1), overwrite: true);
            }
        }

        File.Move(_path, _path + ".1", overwrite: true);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???"
    };

    public void Dispose()
    {
        // Es bleibt kein Handle offen; AppendAllText öffnet und schließt je Zeile.
    }
}
