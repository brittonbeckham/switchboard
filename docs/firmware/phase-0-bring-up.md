# Phase 0 — Firmware bring-up checklist

Safe path to a custom Megalodon build. Product goals live in
`docs/spec-megalodon-firmware.md`. This page is the *how we don’t brick it* list.

## Why not an emulator first?

QMK on this pad drives real USB, EEPROM, and a 16-LED matrix. A software
emulator will not catch the mistakes that brick you (wrong MCU target, wrong
bootloader flash format). What actually protects the device:

1. Correct **rev1 vs rev2** target
2. A **known-good image** on disk before the first custom flash
3. First custom flash is **stock-parity** (behavior unchanged except an identity mark)
4. Bootloader entry always works (top-left held on plug)

Host-side tests come later for *Switchboard protocol* (mock HID), after the pad
still speaks VIA like today.

## Step 1 — Soft backup (keymap)

In Switchboard → Megalodon Pad → **⋯** → **Backup Now**.

Copies live under the app’s backup folder (`pad-*.json`: keys, encoders,
lighting). Restore from the same menu after a flash if EEPROM defaults wipe
your layout.

Also copy the newest `pad-*.json` into `firmware/backups/` if you want it next
to the binary images.

## Step 2 — Identify hardware revision

1. Install [QMK Toolbox](https://github.com/qmk/qmk_toolbox/releases) if needed;
   use **Tools → Install Drivers…** once on Windows.
2. Unplug the pad.
3. Hold the **top-left key** (`R0C0`), plug USB in, keep holding ~1s.
4. Read the Toolbox log:

| Toolbox shows | Target |
|---|---|
| `Atmel … ATmega32U4 (03EB:2FF4)` | `doio/kb16/rev1` |
| `LeafLabs Maple 003 (1EAF:0003)` / STM32Duino | `doio/kb16/rev2` |

Unplug/replug without the key to return to normal (`VID_D010` / `PID_1601`).

Write the rev on a sticky note and in `firmware/backups/BOARD.md`.

## Step 3 — Hard backup (firmware image)

Before any custom flash, put a **restorable image** in `firmware/backups/`:

| Preferred source | Notes |
|---|---|
| Factory / vendor `.bin` for your rev | Best if you still have it |
| Community known-good for that rev | Only if it matches MCU + bootloader |
| Dump from bootloader (if tool allows read) | Rev1 Atmel DFU often readable; rev2 Maple read support varies |

Name files clearly, e.g. `kb16-rev2-stock-pre-switchboard.bin`.

**Do not flash rev1 images on rev2 or the reverse.**

## Step 4 — First custom build (identity-only)

1. Clone QMK (or Vial-QMK if stock is Vial — match what the board already runs).
2. Keyboard: `doio/kb16/<rev>` from Step 2.
3. Keep USB IDs `0xD010` / `0x1601` unless forced otherwise.
4. Keep VIA (or Vial) enabled as on stock.
5. Change **only** something visible and harmless, e.g. OLED boot text
   `SB P0` or a version string in firmware metadata.
6. Build: `qmk compile -kb doio/kb16/<rev> -km default` (keymap name as upstream).
7. Flash via Toolbox with the board in bootloader (Step 2).
8. Replug; confirm OLED tag, then keys / knobs / RGB / Switchboard pad page.

If anything fails: re-enter bootloader and flash the Step 3 backup image.

## Step 5 — Iterate

Add one feature at a time (layer color table → Color Dial → gestures → ripple).
Prefer compile flags so a “safe” keymap can disable experiments.

## Recover if something goes wrong

1. Hold top-left, plug in → bootloader.
2. Flash the hard-backup image from `firmware/backups/`.
3. Soft-restore keymap from Switchboard if needed.

If Toolbox never sees a bootloader device, stop and fix drivers / cable / port
before trying other images.