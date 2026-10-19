# DeskBridge Native HOGP v0.2 — mouse + keyboard

Independent Windows 11 BLE HID over GATT proof of concept (no ADB, PAN, or scrcpy). This is **not yet a validated input device**.

- Mouse report ID 1: buttons, X/Y delta and wheel; command `right`, `left`, `up`, `down`, `click`.
- Keyboard report ID 2: boot-style modifiers/reserved/6 keys; command `a`, `enter`, `space`.
- Two HID Report characteristics (0x2A4D) with Report Reference descriptors (0x2908), plus the report map, device information and battery service.
- Input is produced by test commands only. Real PC keyboard/mouse capture is deliberately not added until Android recognition has been confirmed.

## Windows testing

Download the ZIP artifact from the GitHub Actions build, unzip, run `DeskBridge.NativeHogp.exe`. Disconnect old experimental HID servers. From the phone Bluetooth settings, pair to the PC. At `hogp>`, run `status`, `right`, then open a phone text box and run `a`. Capture the console output if pairing shows a generic PC instead of a keyboard/mouse, or if the report subscriber counts stay 0.

**Windows successful compilation does not prove Android HID profile acceptance**. Depending on the Windows Bluetooth adapter and Android device, advertisement and/or report registration may fail; this needs hardware validation. If testing a different report map from a prior experiment, old Bluetooth pairing information may need to be cleared on both sides.
