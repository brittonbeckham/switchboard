# Spec: Megalodon Pad Editing (Draft 1)

Turn the Megalodon Pad page from a viewer into the primary editor for the pad.
Assign keys, chords, and Switchboard actions directly from Switchboard — VIA
becomes a fallback tool, not the daily driver.

## Goals

1. Click any key cell, knob turn zone, or knob press zone → assignment editor opens.
2. Assign, in one dialog: plain keys, modifier chords, media keys, layer
   operations, ghost keys — or a **Switchboard action** (one click binds the
   pad key to the action end-to-end).
3. Every write is verified (read-back) and reflected in the UI immediately.
4. Never brick or scramble the pad: snapshot before first write of a session,
   one-click restore.

## Non-goals (this round)

- Macro recording/editing (view-only; edit in VIA).
- Tap-dance, per-key RGB, named keycodes in VIA — all custom-firmware territory.
- Layout/matrix redefinition.

## UX

### Assignment editor (modal, opened from any assignment cell)

A single dialog with a segmented choice at top:

| Mode | UI | Result written |
|---|---|---|
| **Key** | searchable list of keys (letters, digits, F1–F24, nav, media, numpad) | basic keycode |
| **Chord** | modifier checkboxes (Ctrl / Shift / Alt / Win, left-right toggle) + key picker | QMK mod-wrapped keycode |
| **Switchboard Action** | list of ActionCatalog actions | next free ghost key (F13–F24) written to pad **and** mapped to the action in settings |
| **Layer** | go-to / while-held / toggle + layer number | QK layer keycode |
| **Nothing** | — | KC_NO (or KC_TRNS on layers 1–3, user choice) |

Footer: current assignment shown, `Write to Pad` (primary), `Cancel`.
Custom label field included — one dialog for assignment + label.

### Feedback & safety

- After write: read the position back; mismatch → red toast, UI keeps truth.
- First write each session: full snapshot saved to
  `%APPDATA%\Switchboard\pad-backups\<timestamp>.json` (all layers, keys,
  encoders). `Restore Backup…` button on the pad page.
- Pad busy/unplugged: dialog blocks writes with a clear message.
- The VIA app must not be open simultaneously (both talking to the same
  endpoint) — detect a failed/garbled handshake and say so plainly.

### Ghost-key auto-binding (the headline feature)

Choosing a Switchboard action:

1. Find the first ghost key F13–F24 not already used (pad-wide scan + settings).
2. Write it to the chosen position.
3. Add `F{n} → action` to `FunctionKeyActions`, re-register hotkeys.
4. Cell renders the action name (action label style, distinct color).

Unassigning such a key offers to release the hotkey mapping too.

## Protocol

VIA protocol v11 (already proven for reads):

| Operation | Command |
|---|---|
| Write key | `0x05 dynamic_keymap_set_keycode(layer, row, col, kc_hi, kc_lo)` |
| Write encoder | `0x15 dynamic_keymap_set_encoder(layer, encoder, clockwise, kc_hi, kc_lo)` |
| Verify | existing read commands (`0x04` / `0x14`) |

Writes go through the existing `MegalodonPad` HID channel, serialized on one
stream, on a worker thread. Keycode encoding = exact inverse of the existing
decoder (`KeycodeName`), extracted into a shared two-way map so the decoder
and encoder can never drift apart.

## Staging

1. **Spike**: write one keycode to a junk position, read back, restore. Proves
   `0x05`/`0x15` on this firmware before any UI exists.
2. Backup/restore plumbing.
3. Assignment editor: Key + Chord modes.
4. Switchboard Action mode with ghost-key auto-binding.
5. Layer mode + encoder writes + polish pass (design review by screenshot).

## Open questions

- [ ] Unassigned on layers 1–3: default to transparent (`▽`) or nothing (`KC_NO`)?
- [ ] Should Action-bound cells be a third color (e.g. purple) to distinguish
      from custom-labeled (green)?
- [ ] Restore backup: whole pad only, or per-layer restore too?
- [ ] Write-protect Layer 0's layer-switch key (prevent stranding)?
