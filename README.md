# Switchboard

Personal Windows customization app. Lives in the tray; double-click for settings.

## Customizations

### Easy-Switch key remapping (Logitech MX Keys S)

Intercepts the three Easy-Switch (host-switch) keys via Logitech HID++ and maps
them to Windows virtual desktops 1/2/3 (configurable, up to desktop 9).

How it works:

- Scans all Logitech HID++ channels: Bolt/Unifying receiver slots 1–7 and direct
  Bluetooth (device index `0xFF`).
- Finds the device exposing feature `0x1B04` (reprogrammable controls v4) with
  control IDs `0xD1`/`0xD2`/`0xD3` (Host Switch Channel 1–3).
- Sends `setCidReporting` with the **divert** flag so presses stop switching
  hosts in firmware and instead arrive as `divertedButtonsEvent` reports.
- On a press, switches virtual desktops by reading the desktop list/current
  desktop from the registry and replaying `Ctrl+Win+Left/Right` via `SendInput`.

Divert is volatile on the keyboard, so it is re-applied every 30 s and
immediately on receiver connect notifications (`0x41`).

## Build & run

```
dotnet build -c Release
.\bin\Release\net9.0-windows\Switchboard.exe
```

Settings live at `%APPDATA%\Switchboard\settings.json`. "Start with Windows" is
a `HKCU\...\Run` entry. Note: Logi Options+ may fight over the same keys — if
presses don't arrive, quit Options+ and rescan.
