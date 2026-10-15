using System.Security.Cryptography;
using System.Text;

namespace BleHid.Core;

/// <summary>
/// Deterministic identity of the HID GATT layout owned by this process.
/// This DOES NOT capture radio-layer pairing keys, device address, or Windows-assigned GATT handles.
/// </summary>
public static class HidProfileFingerprint
{
    public static string Calculate()
    {
        // Enumerate in creation order, including report references. Reordering these elements
        // changes the database shape even when every individual characteristic stays the same.
        var layout = string.Join("|",
            "HID:1812",
            "2A4A:read", "2A4B:read", "2A4C:write-no-response",
            "2A4E:read,write-no-response",
            "2A4D:keyboard-input:2908:1:1",
            "2A4D:keyboard-output:2908:1:2",
            "2A4D:mouse-input:2908:2:1",
            "BATTERY:180F", "2A19:read,notify",
            "DEVICE_INFO:180A",
            "PNP:optional:2A50:read",
            Convert.ToHexString(HidDescriptors.HidInformationValue),
            Convert.ToHexString(HidDescriptors.ReportMapValue),
            HidDescriptors.KeyboardReportId,
            HidDescriptors.MouseReportId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout)));
    }

    public static string DescribePersistedState(string path)
    {
        var current = Calculate();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (File.Exists(path))
            {
                var previous = File.ReadAllText(path).Trim();
                if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                    return $"HID GATT layout unchanged ({current[..16]}); OS GATT handle stability remains unverified";
                // Keep previous record to avoid silently accepting a new profile.
                return $"WARNING HID GATT layout changed: previous={previous}, current={current}. Check bonded Android GATT cache; old record preserved";
            }
            File.WriteAllText(path, current + Environment.NewLine);
            return $"HID GATT layout baseline recorded ({current[..16]}); does not prove Windows handle stability";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"WARNING cannot persist HID profile fingerprint: {ex.Message}";
        }
    }
}
