using System.Globalization;

namespace BleHid.Core;

/// <summary>
/// GATT PnP ID (0x2A50). Never invent a registered vendor ID on behalf of a user.
/// Supply an actually assigned Bluetooth SIG / USB-IF vendor ID explicitly.
/// </summary>
public readonly record struct HidDeviceIdentity(
    byte VendorIdSource, ushort VendorId, ushort ProductId, ushort ProductVersion)
{
    public byte[] ToPnpIdBytes() =>
    [
        VendorIdSource,
        (byte)VendorId, (byte)(VendorId >> 8),
        (byte)ProductId, (byte)(ProductId >> 8),
        (byte)ProductVersion, (byte)(ProductVersion >> 8)
    ];

    public static bool TryParse(string? configuration, out HidDeviceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(configuration)) return false;
        var parts = configuration.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return false;
        if (!byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var source) ||
            source is not (1 or 2)) return false;
        if (!TryHex(parts[1], out var vendor) || vendor is 0 or ushort.MaxValue ||
            !TryHex(parts[2], out var product) ||
            !TryHex(parts[3], out var version)) return false;

        // The PnP version is four binary-coded decimal digits.
        if (Enumerable.Range(0, 4).Any(i => ((version >> (i * 4)) & 0xf) > 9)) return false;

        identity = new HidDeviceIdentity(source, vendor, product, version);
        return true;
    }

    private static bool TryHex(string text, out ushort result)
    {
        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return ushort.TryParse(text, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out result);
    }
}
