using System.Runtime.InteropServices;

namespace DeckSense.Service.Interop;

/// <summary>
/// Minimale Anbindung an Performance Data Helper (PDH).
///
/// Bewusst <c>PdhAddEnglishCounterW</c> statt <c>PdhAddCounterW</c>: Die
/// Indikatornamen sind auf einem deutschen Windows lokalisiert
/// („Prozessorinformationen(_Total)\Prozessorauslastung"). Mit der englischen
/// Variante bleiben die Pfade im Code sprachunabhängig.
/// </summary>
internal static partial class Pdh
{
    internal const uint FormatDouble = 0x00000200;

    /// <summary>Verhindert, dass PDH den Wert bei 100 abschneidet. „% Processor
    /// Utility" darf über 100 liegen.</summary>
    internal const uint FormatNoCap100 = 0x00008000;

    internal const uint StatusValidData = 0x00000000;
    internal const uint StatusNewData = 0x00000001;

    /// <summary>Erster Aufruf eines Ratenindikators: noch kein zweiter Messpunkt.</summary>
    internal const uint InvalidData = 0xC0000BC6;

    internal const uint NoData = 0x800007D5;

    [StructLayout(LayoutKind.Explicit)]
    internal struct FormattedCounterValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    internal static bool IsUsableStatus(uint status)
        => status is StatusValidData or StatusNewData;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint OpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint AddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    internal static partial uint CollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    internal static partial uint GetFormattedCounterValue(
        nint counter,
        uint format,
        out uint counterType,
        out FormattedCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    internal static partial uint CloseQuery(nint query);
}
