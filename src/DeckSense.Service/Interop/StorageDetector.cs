using System.Management;

namespace DeckSense.Service.Interop;

/// <summary>
/// Ein gefundenes Laufwerk, so wie Windows es beschreibt. Enthält nur
/// Geräteeigenschaften - für diese Angaben werden <b>keine SMART-Daten</b>
/// gelesen.
/// </summary>
/// <param name="Serial">Normalisierte Seriennummer, die stabile Kennung.</param>
/// <param name="WmiPath">Objektpfad, über den später die Temperatur gelesen wird.</param>
internal sealed record StorageDeviceInfo(
    string Serial,
    string FriendlyName,
    string DeviceNumber,
    string BusType,
    string MediaType,
    ulong SizeBytes,
    string WmiPath)
{
    public double SizeGigabytes => SizeBytes / 1024d / 1024d / 1024d;

    /// <summary>
    /// USB-Geräte und Laufwerke ohne Größe sind in aller Regel Kartenleser oder
    /// Wechselschächte. Sie liefern keine brauchbare Temperatur und werden
    /// deshalb bei der ersten Einrichtung nicht freigegeben.
    /// </summary>
    public bool LooksLikeFixedDisk
        => SizeBytes > 0 && !string.Equals(BusType, "USB", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Findet die Laufwerke des Systems über <c>MSFT_PhysicalDisk</c>.
///
/// Gelesen werden ausschließlich Geräteeigenschaften: Modell, Seriennummer,
/// Nummer, Bustyp, Größe. Die Temperatur steckt woanders - sie kommt über eine
/// Association und wird nur für freigegebene Laufwerke abgefragt, siehe
/// <see cref="Sensors.Sources.StorageSource"/>.
/// </summary>
internal static class StorageDetector
{
    internal const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";
    internal const string PhysicalDiskClass = "MSFT_PhysicalDisk";

    internal static IReadOnlyList<StorageDeviceInfo> Enumerate()
    {
        var found = new List<StorageDeviceInfo>();

        var scope = new ManagementScope(StorageNamespace);
        var query = new ObjectQuery($"SELECT * FROM {PhysicalDiskClass}");

        using var searcher = new ManagementObjectSearcher(scope, query);
        using ManagementObjectCollection disks = searcher.Get();

        foreach (ManagementBaseObject item in disks)
        {
            using var disk = (ManagementObject)item;

            string serial = Normalize(disk["SerialNumber"] as string);
            if (serial.Length == 0)
            {
                continue;
            }

            found.Add(new StorageDeviceInfo(
                Serial: serial,
                FriendlyName: (disk["FriendlyName"] as string ?? "?").Trim(),
                DeviceNumber: disk["DeviceId"] as string ?? "?",
                BusType: DescribeBusType(disk["BusType"]),
                MediaType: DescribeMediaType(disk["MediaType"]),
                SizeBytes: ToUInt64(disk["Size"]),
                WmiPath: disk.Path.Path));
        }

        return [.. found.OrderBy(d => d.DeviceNumber.Length).ThenBy(d => d.DeviceNumber, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Seriennummern kommen je nach Quelle mit führenden Leerzeichen oder einem
    /// angehängten Punkt. Für den Vergleich wird beides entfernt.
    /// </summary>
    internal static string Normalize(string? serial)
        => serial is null ? string.Empty : serial.Trim().Trim('.').Trim();

    private static ulong ToUInt64(object? value)
    {
        try
        {
            return value is null ? 0UL : Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0UL;
        }
    }

    // Zahlenwerte nach der Dokumentation von MSFT_PhysicalDisk.
    private static string DescribeBusType(object? value) => ToUInt64(value) switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtuell",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "unbekannt"
    };

    private static string DescribeMediaType(object? value) => ToUInt64(value) switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "unbekannt"
    };
}
