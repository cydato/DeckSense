using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DeckSense.Service.Logging;
using DeckSense.Service.Sensors;

namespace DeckSense.Service.Api;

/// <summary>
/// Die JSON-HTTP-Schnittstelle, über die das Stream-Deck-Plugin die Werte holt.
///
/// Bindet <b>ausschließlich an 127.0.0.1</b>, nie an 0.0.0.0. Damit braucht es
/// keine Firewallregel, und von außen ist nichts erreichbar. Es gibt keine
/// Anmeldung und kein CORS - die Beschränkung auf Loopback ist die Abgrenzung.
///
/// Ein Aufruf liest <b>nur</b> den Zwischenspeicher und löst nie eine
/// Hardwareabfrage aus. Sonst würde die Anzahl der Tasten bestimmen, wie oft
/// die Hardware angefasst wird - siehe Entscheidung 0002.
///
/// <para>
/// <b>Serialisiert wird einmal je Schnappschuss, nicht einmal je Anfrage.</b>
/// Die fertige Antwort liegt in einem wiederverwendeten Puffer und wird so
/// lange weitergegeben, wie der Schnappschuss derselbe ist. Derselbe Gedanke
/// wie beim Zwischenspeicher der Werte, eine Ebene höher: Die Anzahl der
/// Abfragen soll die Arbeit nicht bestimmen. Siehe Entscheidung 0022.
/// </para>
/// </summary>
public sealed class HttpApi : IDisposable
{
    private const string SensorsPath = "/api/sensors";

    /// <summary>Abstand zwischen zwei Versuchen, wenn der Port belegt ist.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Die Fehlerantworten sind feste Zeichenketten. Einmal beim Laden
    /// serialisiert, danach nur noch kopiert.
    /// </summary>
    private static readonly byte[] FehlerNurGet = FehlerAntwort("only GET is supported");

    private static readonly byte[] FehlerUnbekannteId = FehlerAntwort("unknown sensor id");

    private static readonly byte[] FehlerUnbekannterPfad = FehlerAntwort("unknown path");

    /// <summary>
    /// Puffer für <c>/api/sensors</c>. Wird nie freigegeben, nur geleert und
    /// neu beschrieben. 8 KB reichen für die vorgesehene Sensorzahl, wachsen
    /// bei Bedarf aber von selbst.
    /// </summary>
    private readonly ArrayBufferWriter<byte> _alleBuffer = new(8 * 1024);

    private readonly Utf8JsonWriter _alleWriter;

    /// <summary>Puffer für <c>/api/sensors/{id}</c>, dieselbe Überlegung.</summary>
    private readonly ArrayBufferWriter<byte> _einzelnBuffer = new(512);

    private readonly Utf8JsonWriter _einzelnWriter;

    /// <summary>
    /// Der Schnappschuss, aus dem <see cref="_alleBuffer"/> entstanden ist.
    /// Solange derselbe kommt, wird der Puffer unverändert weitergegeben.
    /// Verglichen wird über die Referenz: Ein Schnappschuss ist unveränderlich
    /// und wird als Ganzes ausgetauscht, ein neuer ist also immer ein anderes
    /// Objekt.
    /// </summary>
    private SensorSnapshot? _alleQuelle;

    private readonly SnapshotStore _store;
    private readonly ILog _log;
    private readonly int _port;
    private readonly string _prefix;
    private readonly CancellationTokenSource _stopping = new();

    private HttpListener? _listener;
    private Task? _loop;
    private bool _startFailureLogged;
    private bool _disposed;

    public HttpApi(SnapshotStore store, ILog log, int port)
    {
        _store = store;
        _log = log;
        _port = port;
        _prefix = $"http://127.0.0.1:{port}/";
        _alleWriter = new Utf8JsonWriter(_alleBuffer);
        _einzelnWriter = new Utf8JsonWriter(_einzelnBuffer);
    }

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!TryOpenListener())
            {
                // Der Port kann kurz nach einem Neustart noch belegt sein.
                // Lieber still weiter versuchen, als den Dienst zu beenden - // die Sensoren laufen ja.
                try
                {
                    await Task.Delay(RetryDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await AcceptLoopAsync(token).ConfigureAwait(false);
        }
    }

    private bool TryOpenListener()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(_prefix);
            listener.Start();

            _listener = listener;
            _startFailureLogged = false;
            _log.Info($"HTTP-API hört auf {_prefix}");
            return true;
        }
        catch (Exception error)
        {
            if (!_startFailureLogged)
            {
                _startFailureLogged = true;
                _log.Error($"Die HTTP-API konnte {_prefix} nicht öffnen. Ist der Port {_port} belegt? "
                           + "Der Dienst läuft weiter und versucht es alle 15 s erneut; "
                           + "das Stream-Deck-Plugin zeigt bis dahin „--\".", error);
            }

            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        HttpListener listener = _listener!;

        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _log.Warn("Die HTTP-API hat die Verbindung verloren und wird neu geöffnet.", error);
                CloseListener();
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception error)
            {
                // Eine kaputte Anfrage darf weder den Dienst noch die Schleife beenden.
                _log.Warn("Eine Anfrage an die HTTP-API ist fehlgeschlagen.", error);
                TrySetStatus(context, HttpStatusCode.InternalServerError);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // Gegenstelle ist weg. Nicht weiter schlimm.
                }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Write(context, HttpStatusCode.MethodNotAllowed, FehlerNurGet);
            return;
        }

        string path = (request.Url?.AbsolutePath ?? string.Empty).TrimEnd('/');
        SensorSnapshot snapshot = _store.Current;

        if (string.Equals(path, SensorsPath, StringComparison.OrdinalIgnoreCase))
        {
            WriteAllSensors(context, snapshot);
            return;
        }

        if (path.StartsWith(SensorsPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            string id = Uri.UnescapeDataString(path[(SensorsPath.Length + 1)..]);

            if (snapshot.TryGet(id, out SensorReading reading))
            {
                WriteSingleSensor(context, reading);
            }
            else
            {
                Write(context, HttpStatusCode.NotFound, FehlerUnbekannteId);
            }

            return;
        }

        Write(context, HttpStatusCode.NotFound, FehlerUnbekannterPfad);
    }

    /// <summary>
    /// Gibt die Gesamtliste aus. Serialisiert nur, wenn der Schnappschuss ein
    /// anderer ist als beim letzten Mal - bei mehreren Tasten oder mehreren
    /// Abfragen pro Sekunde fällt die Arbeit also genau einmal an.
    ///
    /// <para>
    /// Zugriff auf die Puffer braucht keine Sperre: Anfragen werden in der
    /// Annahmeschleife eine nach der anderen bearbeitet (siehe
    /// <see cref="AcceptLoopAsync"/>), es gibt also nur einen Bearbeiter.
    /// </para>
    /// </summary>
    private void WriteAllSensors(HttpListenerContext context, SensorSnapshot snapshot)
    {
        if (!ReferenceEquals(_alleQuelle, snapshot))
        {
            _alleBuffer.ResetWrittenCount();
            _alleWriter.Reset(_alleBuffer);

            _alleWriter.WriteStartObject();
            WriteTimestamp(_alleWriter, "generatedAt", snapshot.GeneratedAt);
            _alleWriter.WriteString("version", AppInfo.Version);
            _alleWriter.WriteStartArray("sensors");

            foreach (SensorReading reading in snapshot.Readings)
            {
                WriteSensorObject(_alleWriter, reading);
            }

            _alleWriter.WriteEndArray();
            _alleWriter.WriteEndObject();
            _alleWriter.Flush();

            _alleQuelle = snapshot;
        }

        Write(context, HttpStatusCode.OK, _alleBuffer.WrittenSpan);
    }

    /// <summary>
    /// Einzelabfrage. Hier wird nicht zwischengespeichert - die IDs sind
    /// beliebig viele, und das Plugin holt alles über die Gesamtliste. Der
    /// Puffer wird trotzdem wiederverwendet.
    /// </summary>
    private void WriteSingleSensor(HttpListenerContext context, SensorReading reading)
    {
        _einzelnBuffer.ResetWrittenCount();
        _einzelnWriter.Reset(_einzelnBuffer);

        WriteSensorObject(_einzelnWriter, reading);
        _einzelnWriter.Flush();

        Write(context, HttpStatusCode.OK, _einzelnBuffer.WrittenSpan);
    }

    private static void WriteSensorObject(Utf8JsonWriter writer, SensorReading reading)
    {
        writer.WriteStartObject();
        writer.WriteString("id", reading.Descriptor.Id);
        writer.WriteString("name", reading.Descriptor.Name);
        writer.WriteString("category", CategoryName(reading.Descriptor.Category));

        if (reading.Available && reading.Value.HasValue)
        {
            // Zwei Nachkommastellen reichen für jede Anzeige und halten die
            // Antwort frei von Gleitkommarauschen wie 2.2999999523162842.
            writer.WriteNumber("value", Math.Round(reading.Value.Value, 2));
        }
        else
        {
            // Ein fehlender Wert ist null, niemals 0.
            writer.WriteNull("value");
        }

        writer.WriteString("unit", reading.Descriptor.Unit);
        writer.WriteBoolean("available", reading.Available);

        // Nur wenn der Wert tatsächlich gehalten wird. Ein Feld, das in
        // neunundneunzig von hundert Antworten „false" sagt, bläht jede
        // Antwort auf, ohne etwas mitzuteilen - und ein fehlendes Feld ist in
        // JSON genauso eindeutig. Siehe Entscheidung 0025.
        if (reading.Stale)
        {
            writer.WriteBoolean("stale", true);
        }

        WriteTimestamp(writer, "updatedAt", reading.UpdatedAt);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Feste Kennungen für die API. Bewusst nicht aus dem Enum-Namen
    /// abgeleitet: Eine Umbenennung im Code darf die Schnittstelle nicht
    /// verändern.
    /// </summary>
    private static string CategoryName(SensorCategory category) => category switch
    {
        SensorCategory.Cpu => "cpu",
        SensorCategory.Memory => "memory",
        SensorCategory.Gpu => "gpu",
        SensorCategory.Storage => "storage",
        SensorCategory.Rtss => "rtss",
        _ => "other"
    };

    /// <summary>
    /// Schreibt den Zeitstempel ohne Zwischenzeichenkette. Formatiert wird auf
    /// dem Stapel; bei elf Sensoren im Sekundentakt spart das elf Objekte je
    /// Serialisierung.
    /// </summary>
    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset value)
    {
        // Ein fehlender Zeitstempel wird als Epoche gemeldet, nicht als
        // Jahr 1 - das Plugin rechnet damit.
        DateTime zeit = value == DateTimeOffset.MinValue
            ? DateTime.UnixEpoch
            : value.UtcDateTime;

        Span<char> puffer = stackalloc char[32];

        if (zeit.TryFormat(puffer, out int laenge, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
        {
            writer.WriteString(name, puffer[..laenge]);
        }
        else
        {
            // Kommt bei festem Format und fester Länge nicht vor; dann aber
            // lieber ein gültiger Wert als eine Ausnahme.
            writer.WriteString(name, "1970-01-01T00:00:00.000Z");
        }
    }

    /// <summary>Serialisiert eine Fehlerantwort. Nur beim Laden der Klasse.</summary>
    private static byte[] FehlerAntwort(string message)
    {
        var buffer = new ArrayBufferWriter<byte>(64);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(HttpListenerContext context, HttpStatusCode status, ReadOnlySpan<byte> payload)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;

        // Der Zwischenspeicher ändert sich jede Sekunde. Nichts davon darf
        // zwischengespeichert werden.
        response.Headers["Cache-Control"] = "no-store";

        response.OutputStream.Write(payload);
    }

    private static void TrySetStatus(HttpListenerContext context, HttpStatusCode status)
    {
        try
        {
            context.Response.StatusCode = (int)status;
        }
        catch (Exception)
        {
            // Antwort war schon unterwegs.
        }
    }

    private void CloseListener()
    {
        try
        {
            _listener?.Close();
        }
        catch (Exception)
        {
            // Nichts zu retten.
        }

        _listener = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Cancel();
        CloseListener();

        _alleWriter.Dispose();
        _einzelnWriter.Dispose();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Beim Beenden ist das gleichgültig.
        }

        _stopping.Dispose();
    }
}
