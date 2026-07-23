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

## Interaction Walkthrough (the full experience, click by click)

### Discovering editability

You open the Megalodon Pad page. Every assigned cell — keycaps, knob turns,
knob presses — shows a hand cursor on hover and lifts slightly (background
brightens one step, border darkens one step). Unassigned cells do the same:
in edit-capable Switchboard, "empty" is an invitation, not dead space. A
subtle hint line under the subtitle reads: *"Click any position to change it."*

### Opening the editor

You click the `Delete` keycap on Layer 0. The assignment editor opens as a
compact modal centered on the Switchboard window (not the screen — the app
holds together as one object). Title: **"Layer 0 · Key R2C3"** with the
current assignment echoed beneath it: `Currently: Delete`.

Across the top: five segmented buttons — **Key · Chord · Action · Layer ·
Clear** — with the segment matching the current assignment pre-selected
(here: *Key*, with `Delete` highlighted in the key list below).

### Mode: Key

A search box with focus already in it, above a scrollable grid of key chips
grouped under small headers (Letters, Digits, Function, Ghost F13–F24,
Navigation, Media, Numpad). Typing filters instantly — typing `vol` collapses
the list to Volume Up / Volume Down / Mute. Clicking a chip selects it
(accent fill); the footer preview updates live: `Will write: Volume Up`.

### Mode: Chord

Four modifier toggle buttons (Ctrl, Shift, Alt, Win) styled like keycaps —
click to press them "down". Below, the same searchable key picker for the
base key. The preview composes live as you toggle: `Will write:
Ctrl+Shift+M`. If the result matches the known-chords library, the meaning
appears next to it: `Ctrl+Shift+M — (Toggle Mic (Apps))`.

### Mode: Action (the headline)

A list of Switchboard actions, each with name and one-line description
(Mute / Unmute Microphone, Switch To Desktop 1–9, Launch Or Focus
Calculator, Toggle Focus Mode, Open Settings). Selecting one shows the plan
in plain language in the footer:

> *Will write **F17** to this key and map **F17 → Mute / Unmute Microphone**
> in Switchboard.*

F17 being the next unused ghost key — chosen automatically, shown so nothing
is mysterious.

### Mode: Layer

Three radio choices — *Go To*, *While Held*, *Toggle* — and a layer number
selector (0–3). Preview: `Will write: Layer 2 While Held`. If this would
overwrite the only layer-switch key on the current layer, a warning line
appears: *"This is this layer's only way out — writing it may strand the
pad on Layer 2."* (Write still allowed; you're warned, not babysat.)

### Mode: Clear

One choice on layers 1–3: *Transparent (fall through to Layer 0)* vs
*Nothing (key does nothing)*. On Layer 0, just *Nothing*.

### The label field

Below every mode: a single text field, **Label (optional)** — pre-filled
with the existing custom label if one exists. One dialog handles what the
key *does* and what you *call* it.

### Writing

You click **Write to Pad** (accent-filled primary button; Cancel is plain).
The button becomes a spinner for the ~100 ms round-trip. Under the hood:
snapshot-if-first-write → write → read-back verify. On success the dialog
closes and the cell repaints with its new assignment — no full-page reload,
just that cell. A quiet toast in the page corner: *"R2C3 → Volume Up ✓"*.

On verify-mismatch the dialog stays open with a red banner: *"The pad
reports a different value than written — VIA may be open. Close VIA and
retry."* Nothing in Switchboard's UI lies: the cell keeps showing what the
pad actually contains.

### Backup & restore

First write of the session silently saves a full-pad backup. The page footer
gains a small line: *"Backup saved 3:41 PM · Restore…"*. Clicking
**Restore…** lists this session's backups (timestamped), one click restores
the whole pad, page re-reads, toast confirms.

### Unbinding an Action key

Clearing (or overwriting) a cell that holds an action-bound ghost key pops
one extra question in the dialog footer: *"F17 is mapped to Mute / Unmute
Microphone in Switchboard. Release that mapping too?"* — Yes keeps the two
systems consistent; No leaves the hotkey mapping for another key to claim.

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
