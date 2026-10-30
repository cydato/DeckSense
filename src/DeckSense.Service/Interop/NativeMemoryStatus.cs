using System.Runtime.InteropServices;

namespace DeckSense.Service.Interop;

/// <summary>
/// <c>GlobalMemoryStatusEx</c> aus kernel32. Billiger und genauer als ein
/// Leistungsindikator oder eine WMI-Abfrage für denselben Wert.
/// </summary>
internal static partial class NativeMemoryStatus
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// Liest den Speicherstatus. Wirft <see cref="System.ComponentModel.Win32Exception"/>,
    /// wenn die API fehlschlägt.
    /// </summary>
    internal static MemoryStatusEx Query()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "GlobalMemoryStatusEx ist fehlgeschlagen.");
        }

        return status;
    }
}
