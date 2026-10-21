# DeskBridge MOUSE ONLY — movement-only Android recognition test

This is an **independent Windows executable** (`DeskBridge-MOUSE-ONLY.exe`). The existing full DeskBridge v0.13 source and artifact remain unchanged.

## What this build declares

- BLE HID service `0x1812`, with exactly one HID application collection: **Mouse** (`Usage Page 0x01, Usage 0x02`).
- Exactly one input Report (`0x2A4D`), Report ID `1` and Report Reference descriptor `0x2908 = [1,1]`.
- Mouse X/Y relative movement. The standard 4-byte mouse report layout is retained for host compatibility, but **button and wheel fields are always zero**. The program never sends clicks/wheel.
- **No keyboard HID Report**, no Boot Keyboard, no gamepad.
- Same Windows BLE GATT provider and `EncryptionRequired` policy as the full comparison build.
- Windows BLE API controls advertising and Appearance; the app cannot force an Android Bluetooth list icon.

## Usage

1. Close other DeskBridge HID processes. The builds share a single-instance lock and the same Windows Bluetooth adapter.
2. Start `DeskBridge-MOUSE-ONLY.exe`. The console title and log prefix clearly say `MOUSE ONLY`.
3. Pair/connect phone if required. Windows/Android may cache an older composite GATT profile for this same adapter. A **single clean pairing** may be needed to distinguish the new mouse-only profile; do not delete bonds repeatedly.
4. Press **F8** to send mouse movement to phone, press **F8** again to return to PC; **F9** is emergency return.
5. In the `mouse-only>` console, `status` shows advertisement/subscriber status; `restart` reattempts advertising if no subscriber; `restart!` forces advertiser restart; `quit` exits.

## Important distinction

The expected Android **functional input class** is mouse. The Bluetooth device icon is chosen by Android based on other information as well and **may still resemble a game controller** without proving it is registered as a gamepad.

This build is a controlled experiment on host interpretation. It is not a claimed solution to the original pairing reliability issue. Changing the GATT profile under the same Windows Bluetooth hardware identity can interact with Android's cached service data.

## Source and build

`native-hogp-mouse-only/`, compiled independently by `.github/workflows/deskbridge-mouse-only-windows.yml`.
