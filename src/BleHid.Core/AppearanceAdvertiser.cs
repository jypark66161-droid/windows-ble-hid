using Windows.Devices.Bluetooth.Advertisement;

namespace BleHid.Core;

/// <summary>
/// Compatibility shim for the historic CLI experiment.
/// The WinRT advertisement publisher does not permit the system-reserved
/// Appearance (AD 0x19) and service-list (AD 0x03) fields. Pretending to
/// publish them would give false confidence that Android sees a keyboard.
/// </summary>
public sealed class AppearanceAdvertiser : IDisposable
{
    public const ushort KeyboardAppearance = 0x03C1;
    public const ushort MouseAppearance = 0x03C2;

    public event Action<string>? Log;

    public BluetoothLEAdvertisementPublisherStatus Status =>
        BluetoothLEAdvertisementPublisherStatus.Created;

    public bool Start(ushort appearance = KeyboardAppearance)
    {
        Log?.Invoke("[appr] Windows GATT advertising does not expose a custom GAP appearance; " +
            "reserved fields 0x19/0x03 cannot be set via BluetoothLEAdvertisementPublisher. " +
            "No additional advertisement was started.");
        return false;
    }

    public void Dispose() { }
}
