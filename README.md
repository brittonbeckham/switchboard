# Switchboard

A Windows tray app for power users who want real control over their OS and
their macro pad — global hotkeys, window/desktop management, focus tooling,
and a generic, JSON-driven action system for anything a plain keystroke can't
do. It's built out around a specific device right now, a DOIO KB16 ("Megalodon")
macropad — 16 keys across 4 layers, 3 rotary encoders, driven directly over its
raw VIA HID protocol — but the customization layer underneath (actions,
hotkeys, window/desktop control) isn't pad-specific.

## Megalodon Pad

The Settings window's Megalodon Pad page mirrors the physical device: a 4×4
keycap grid, two small knobs, one big knob, and a layer readout styled after
the pad's own onboard OLED (digits 1..N, the active one shown as an inverted
pill, prev/next chevrons to page through them).

- **Live editing.** Click any key or knob zone (turn-left / turn-right /
  press) to stage an assignment — a key, a modifier chord, a layer switch, an
  Action, or clear. Staged changes glow amber until "Write to Pad" commits
  them; a pending-changes bar shows the count and lets you discard.
- **Drag and drop.** Drag one key onto another to move or swap assignments,
  with live visual feedback during the drag.
- **Universal search.** A single search box in the assignment dialog finds
  chords (by friendly name), actions, keys, and layer switches together.
- **Ghost-key actions.** Actions are wired to the pad via invisible F13–F24
  keys ("ghost keys"), optionally modifier-wrapped for far more than 12 slots,
  allocated automatically — you only ever pick a physical position and an
  action, never see the underlying keycode.
- **Backups.** Every pad read auto-saves a rolling backup (keymap + encoders +
  RGB lighting config) to `%APPDATA%\Switchboard\pad-backups\`, deduped so
  identical reads don't pile up. Restorable from the Pad page.

## Custom actions

Beyond the built-in actions (mic mute, Calculator launch/focus, focus mode,
open settings, virtual desktop switching, move the active window to the next
desktop), Switchboard has a small generic step interpreter so new actions can
be built **without writing code** — assembled visually in an in-app "Create
New Action" wizard, persisted as JSON, and picked up immediately by the
Action picker and search index.

Step kinds:

| Step | Does |
|---|---|
| `focus-window` | Focus a running app by process name; `process\|launch-command` also launches it first if it isn't running |
| `send-keys` | Send a named key/chord (Ctrl/Shift/Alt/Win) via `SendInput`, or type literal text |
| `hold-key` / `release-key` | Hold a modifier down across separate action invocations (e.g. one action per knob-turn direction) with an automatic timeout — a key can never be left stuck down |
| `run` | Shell-execute any command line, script, file, or URL |
| `window` | Act on the foreground window: pin/unpin on top, maximize/minimize/restore/close, opacity, move to next monitor |
| `run-action` | Call another action by id, for composing chains (depth-capped against accidental cycles) |
| `sleep` / `clear-field` | Pause N ms / select-all-then-delete |

Example: the pad's Left Knob drives real Alt-Tab cycling using genuine
hardware modifier keys (Press = Left Alt, Turn = Tab / Shift+Tab written
directly to the pad) rather than software key injection — Windows' native
Alt-Tab switcher ignores injected repeat keystrokes, so this only works
because the keys are real.

## Other pieces

- **Focus Mode** — dims (or GPU-blurs) every window except the active one, a
  single layered overlay kept just behind the foreground window, updated via
  WinEvent hooks.
- **Key HUD** — an on-screen popup stack showing which pad key/knob was just
  pressed and what it's mapped to, matched to the physical device via its Raw
  Input device path (so main-keyboard presses don't trigger it).
- **Virtual desktop switching** — reads the desktop list/current desktop from
  the registry and replays `Ctrl+Win+Left/Right`, or moves a specific window
  to a specific desktop via `IVirtualDesktopManager`.
- **Detector mode** (`--detector`) — diverts every divertable key on every
  HID++ device and logs raw events, for reverse-engineering new devices.
  (Logitech's MX Keys S Easy-Switch keys were investigated this way and found
  to be firmware-hardcoded non-divertable — they emit nothing to the PC to
  intercept, so that idea is dead and not implemented.)

## Build & run

```
dotnet build -c Release
.\bin\Release\net9.0-windows10.0.22621.0\Switchboard.exe
```

Settings live at `%APPDATA%\Switchboard\settings.json`; custom actions at
`%APPDATA%\Switchboard\custom-actions.json`. "Start with Windows" is a
`HKCU\...\Run` entry.

Useful launch args: `--settings "<page name>"` opens Settings straight to a
page; `--detector` runs detector mode instead of the normal service.
