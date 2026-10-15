# BLE HID identification and reconnection engineering notes

This project exposes a Windows host as a BLE HID-over-GATT peripheral. A BLE ACL link being Connected is **not** evidence that Android installed/reattached a keyboard or that keystrokes arrived.

## Profile work

The HID-over-GATT profile calls for HID (0x1812), Battery (0x180F) and Device Information (0x180A) services. This upgrade adds the missing Device Information service. PnP ID (0x2A50) requires a vendor ID assigned by Bluetooth SIG or USB-IF. The program **must not spoof another manufacturer's identity** merely to make Android label it as a keyboard.

If you own an assigned vendor ID and product identifier, you may enable a PnP ID by setting the environment variable before starting the program:

`BLEHID_PNP_ID=1:1234:0001:0100`

Format: `<vendor source 1=Bluetooth SIG or 2=USB-IF>:<actual assigned vendor hex>:<product hex>:<BCD version hex>`.

The example is only a format example. Do **not** use the example vendor number without permission. When it is not configured, the engine still exposes a Device Information service and logs that its PnP ID is not populated; this is an **incomplete** HOGP identity rather than a claim of full compliance.

## GAP appearance / Android UI

Windows' `GattServiceProviderAdvertisingParameters` does not expose a custom local name or appearance. `BluetoothLEAdvertisementPublisher` prohibits manually publishing system-reserved Appearance (AD type 0x19) or Service UUID list (0x03), so creating a second publisher with those fields is not a legitimate fix.

The 'appearance' legacy CLI command explains the Windows API limitation instead of attempting an invalid advertisement. A different controller stack/hardware backend with explicit GAP support is needed to control those fields independently.

## Recovery acceptance

1. Check HID service and Device Information GATT reads.
2. Confirm encrypted pairing and fresh input report subscription states.
3. Distinguish ACL Connected, subscribed, sent notification, and **phone actually receiving input**.
4. Never delete all pairings as a recovery shortcut; restrict any user-approved re-pairing to the affected phone.
5. Existing Android bonds may cache GATT handles. Adding the Device Information service changes the database; compare a new pairing and existing bond, without claiming that link reconnection is proof of HID functionality.

The CI is a compile/unit-test gate. Real Android pairing, HID host recognition, and post-disconnect re-use require physical Windows+Android testing.
