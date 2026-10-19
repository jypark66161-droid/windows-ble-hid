# Native HOGP proof of concept
Independent minimal Win11 BLE HOGP **mouse-only** prototype. No ADB, PAN or scrcpy. The Windows GATT API cannot guarantee Android classifies this PC as a HID mouse; successful build or advertising is not functional verification.

1. Download the Windows build from GitHub Actions artifacts.
2. Extract ZIP, run DeskBridge.NativeHogp.exe in Windows Terminal.
3. Pair from Android Bluetooth settings.
4. Type 'status', then 'right' and verify pointer movement.

If the app outputs Started but Android treats PC as an ordinary PC, record console output: that means advertisement worked but Android HID recognition did not.
No physical PC mouse interception yet. This is a classification and report-notification test. Stop prior BLE HID servers before testing.
