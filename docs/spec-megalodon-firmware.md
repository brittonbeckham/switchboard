# Spec: Megalodon Custom Firmware (Draft 1)

Custom QMK firmware for the DOIO KB16 (“Megalodon”), forked from the stock
board support, so Switchboard and the pad can work as one system — not two
apps fighting over VIA.

Stock firmware stays the baseline: same USB IDs, same matrix, same VIA keymap
editing from Switchboard. We add a **Switchboard channel** (raw HID / VIA
custom) for lighting, layer awareness, and richer key behavior.

## Goals

1. **Layer-aware lighting** — each layer has a color (and later an effect);
   the pad applies it the moment the layer changes, even if Switchboard is closed.
2. **On-pad color dial** — hold corner chords and turn the top two knobs to
   set the *current layer’s* color: bottom corners = palette + hue; left
   corners = saturation + brightness (see Color Dial below).
3. **Host lighting commands** — Switchboard can set / query layer colors and
   live wash over HID (Write to Pad stays the source of truth in the UI).
4. **Gesture keys** — tap / hold / double-tap (and later combos) can mean
   different things, reported clearly to the PC when the action is a Switchboard
   action.
5. **Pad → PC events** — Switchboard learns “went to layer 2”, “key held”,
   “double-tapped position R1C2” without guessing from ghost F-keys.
6. **Stay flashable and recoverable** — bootloader path, VIA still works for
   basic keymap edits, never brick a daily driver.

## Non-goals (this round)

- Replacing Switchboard for Windows automation (launch app, focus window,
  Wispr, clipboard). Firmware signals; the PC still executes OS work.
- Full RGB matrix artist UI / per-frame animations authored on-device.
- Changing the physical matrix layout or encoder count.
- Shipping unsigned “mystery” builds — plan assumes Britton builds from a
  known QMK fork with a documented flash procedure.

## Hardware / base assumptions

| Item | Value |
|---|---|
| Device | DOIO KB16 / Megalodon |
| USB | VID `0xD010`, PID `0x1601` (keep unless forced to change) |
| Matrix | 4×4 keys + 3 encoders (press on matrix col 4) |
| Lighting today (VIA host) | Global hue/sat (+ brightness / effect / speed) via custom `0x07`/`0x08`/`0x09` |
| Lighting hardware | **Full RGB matrix — 16 WS2812-class LEDs, one under each key** (QMK `RGB_MATRIX`, `DRIVER_LED_TOTAL 16`) |
| Keymap host | Switchboard already speaks VIA v11 (`0x04`/`0x05` keys, `0x14`/`0x15` encoders) |
| Upstream QMK tree | `keyboards/doio/kb16` (rev1 AVR / rev2 STM32); Megalodon = KB16 with VID `0xD010` PID `0x1601` |

Fork `doio/kb16` (rev matching Britton’s board), keep VIA enabled, add Switchboard
protocol beside it.

### Research note — per-LED control is real

Stock Switchboard only talks to the **global** VIA lighting channel, which is why
today’s UI feels like one wash. Under the hood the official QMK port already
defines a full matrix:

- 16 LEDs mapped 1:1 to the 4×4 grid (`g_led_config` / info.json layout)
- `RGB_MATRIX_KEYPRESSES` enabled in vendor config
- Built-in reactive modes already compiled in upstream (splash / nexus / wide
  reactive, etc.)

So custom firmware can call `rgb_matrix_set_color(led_index, r, g, b)` (and
custom effects) per key. Encoder / OLED areas are **not** in that 16-LED map —
ripples are key-grid only unless hardware review finds more LEDs.

VIA’s desktop app still mostly exposes global hue/sat for this board; per-LED
artist control is a Switchboard + custom firmware job, not stock VIA.

---

## Split of responsibilities

| Concern | Firmware | Switchboard |
|---|---|---|
| Active layer | Source of truth; applies layer color | Shows layer; writes color table |
| LED wash per layer | Stores table in EEPROM; applies on `layer_state_set` | Color picker UI; Write to Pad |
| Per-key RGB / ripples | Drives all 16 matrix LEDs; custom effects | Effect picker; optional per-key accents |
| Tap / hold / double-tap | Detects gesture; may send different HID reports | Maps gesture → action |
| Go to Layer | Native QMK (as today) | No longer needs ghost-F macros for color |
| Run app / focus window / Wispr | — | Host actions |
| Keymap edit | VIA dynamic keymap | Existing pad editor |

---

## Protocol sketch (Switchboard channel)

Use VIA **custom** get/set (`0x08` / `0x07`) under a dedicated channel id
(e.g. `0x53` = ‘S’), *or* a separate raw-HID usage if custom space is crowded.
Exact opcode bytes are implementation detail; the command set is what matters.

### Host → pad (commands)

| Command | Purpose |
|---|---|
| `GET_PROTO_VERSION` | Negotiate feature set |
| `GET_ACTIVE_LAYER` | Highest layer / layer bitmask |
| `GET_LAYER_COLOR(layer)` | RGB or HSV for that layer |
| `SET_LAYER_COLOR(layer, rgb)` | Update EEPROM table |
| `APPLY_LAYER_COLOR(layer)` | Paint wash now (optional; usually automatic) |
| `SET_LIVE_WASH(rgb)` | Temporary override (dictation mode, alerts) |
| `CLEAR_LIVE_WASH` | Back to active-layer color |
| `SET_MATRIX_EFFECT(id)` | Solid layer wash / reactive ripple / … |
| `SET_LED(index, rgb)` | Direct per-key set (debug / accents; optional) |
| `GET_KEY_GESTURE_CONFIG(row,col)` / `SET_…` | Later: tap vs hold bindings metadata if stored on-pad |

### Pad → host (events)

Prefer **push** on change (raw HID notify) so Switchboard does not poll forever:

| Event | Payload |
|---|---|
| `LAYER_CHANGED` | `layer` (and optional previous) |
| `LAYER_COLOR_CHANGED` | `layer` + color (after Color Dial or host set) |
| `GESTURE` | `row, col` or encoder id + `kind` (`tap` / `hold_start` / `hold_end` / `double_tap`) |
| `READY` | After boot / after EEPROM load |

If push is awkward first, poll `GET_ACTIVE_LAYER` at ~10–20 Hz only while
Switchboard is open — enough for color sync; gestures still need push or
distinct keycodes.

---

## Color Dial (built-in, no host required)

Hands-on way to set the **active layer’s** wash while looking at the pad LEDs.
Two hold chords, **same two small top knobs** remapped while held. Coarse
steps — useful colors, not 16 million RGB values. Chords stay on the left /
bottom edge so a left-hand hold does not cover the lit keys you are watching.

### Layout (4×4 grid, column 0 = far left)

```
R0C0  R0C1  R0C2  R0C3     enc0 (top-left small)   enc1 (top-right small)
  ^                                 \                 /
  │                                  \     OLED      /
R1C0  R1C1  R1C2  R1C3                \             /
R2C0  R2C1  R2C2  R2C3                 enc2 (big — unused by dial)
R3C0  R3C1  R3C2  R3C3
  ^                 ^
  │                 └─ bottom-right corner
  └─ left column corners (R0C0 + R3C0) and bottom-left of bottom chord
```

### Chord map — same knobs, different holds

| Hold (both keys down) | Left small (enc 0) | Right small (enc 1) | Job |
|---|---|---|---|
| **Bottom corners** `R3C0` + `R3C3` | **Main colors** (palette) | **Hue** | *What* color |
| **Left corners** `R0C0` + `R3C0` | **Saturation** | **Brightness** (value) | *How* vivid / bright |

While either chord is held, enc 0 and enc 1 normal bindings are suppressed;
only the dial runs. Big knob (enc 2) stays on its normal binding. If both
chords somehow overlap (both need `R3C0`), **bottom (color) wins**. Release →
knobs return to normal.

### What each axis does

**Bottom chord — color**

- **Left — main colors.** One detent = one named stop (~12–16), e.g.  
  `Red → Orange → Yellow → Lime → Green → Teal → Cyan → Azure → Blue → Violet → Magenta → Pink → Warm white → Soft white`  
  Palette entries are vivid / mid-bright HSV; list is tunable.
- **Right — hue.** Step hue by a coarse chunk (draft ±8 on QMK 0–255 ≈ ~11°),
  wrap. Saturation and brightness stay put. After a palette pick, hue walks
  off that stop so you can land between named colors.

**Left chord — vividness / brightness**

- **Left — saturation.** Step sat (draft ±16, clamp 0–255). Gray ↔ full color.
- **Right — brightness (value).** Step value (draft ±16, clamp 0–255). Dim ↔
  full. This is the *layer wash* brightness stored with the layer color —
  distinct from any global RGB matrix brightness slider if we keep both.

### Behavior

- Live update the RGB matrix on every detent.
- Write the new HSV into the **current layer’s** EEPROM color slot (debounced
  ~500ms after last turn so we don’t burn EEPROM).
- Force a solid / predictable effect while dialing so the wash is readable.
- Optional OLED hint while dialing: `BLUE` / `HUE 140` / `SAT 200` / `VAL 180`
  (Phase 1 nicety).
- If Switchboard is connected, emit `LAYER_COLOR_CHANGED` so the UI swatch updates.

### Why this pairing

Left-hand hold on the left edge (or bottom edge) leaves most of the 4×4 lit
and visible. Bottom = pick the color; left = dial sat / brightness. Same two
knobs every time — muscle memory for “left / right small,” only the hold
changes the meaning. Big knob stays free for volume / scroll / whatever that
layer already uses.

### Implementation notes

- Detect `R3C0`+`R3C3` and `R0C0`+`R3C0` in matrix scan / `process_record_user`.
- In `encoder_update_user`, when a dial chord is active, route enc 0 / enc 1
  per the table above; leave enc 2 alone.
- Palette is a flash-resident HSV table; bottom-chord left knob indexes it (wrap).
- Hue ±8 wrap; sat/val ±16 clamp — tunable later via host if needed.

---

## Feature plan

### Phase 0 — Bring-up (safety first)

Goal: flash something that behaves like stock, with a known restore path.
No Color Dial / gestures yet.

**Ladder (do in order)**

1. **Soft backup (now)** — Switchboard pad JSON (keymap + encoders + lighting).
   Restores *settings* after a good flash; does not replace a firmware image.
2. **Identify revision** — Hold top-left key while plugging USB; QMK Toolbox shows:
   - `Atmel … ATmega32U4 (03EB:2FF4)` → **rev1**
   - `LeafLabs Maple 003 (1EAF:0003)` → **rev2** (most Megalodon / modern KB16)
   Wrong rev firmware can soft-brick until the matching image is restored.
3. **Hard backup** — Keep a known-good `.bin` / `.hex` for that rev (factory image
   or a dump before first custom flash). Store under `firmware/backups/` (gitignored).
4. **Host checks, not a full emulator** — There is no trustworthy “fake pad” that
   proves a flash won’t brick. Safety is: correct rev, stock-parity first build,
   bootloader always reachable, hard backup on disk. Later: Switchboard protocol
   unit tests against a mock HID device before enabling new opcodes on hardware.
5. **First custom image = identity-only** — Fork upstream `doio/kb16/<rev>`, keep
   VID/PID `D010`/`1601`, VIA on, same matrix/RGB. Only change: OLED string or
   a compile-time `SWITCHBOARD_FW_TAG` so we can see the new build took. Keymap
   and lighting behavior match stock.
6. **Smoke test** — Keys, knobs, RGB wash, Switchboard pad page still reads/writes.
   Then iterate features behind `#ifdef` / keymap flags.

See `docs/firmware/phase-0-bring-up.md` for the checklist.

### Phase 1 — Layer colors + Color Dial

**Firmware**

- EEPROM table: color per layer (store as QMK HSV so the dial matches the LEDs).
- On `layer_state_set_user`: set RGB matrix to that layer’s color.
- Commands: get/set layer color, get active layer; optional `LAYER_CHANGED` /
  `LAYER_COLOR_CHANGED` events.
- **Color Dial** (see above): corner chords + top two knobs for palette / hue /
  sat / brightness; persist after a short debounce.
- Keep global brightness / effect / speed unless we explicitly freeze effect to
  solid while dialing or for layer washes.

**Switchboard**

- Existing Layer color UI writes through the new commands on Write to Pad.
- Optional: live preview = `SET_LIVE_WASH` while picking; Write commits EEPROM.
- Listen for `LAYER_COLOR_CHANGED` to refresh swatches after an on-pad dial.
- Drop any ghost-F “fake layer signal” ideas once Phase 1 works.

**Done when:** pressing Go to Layer changes LEDs with Switchboard quit, and you
can hold corner chords + turn the top knobs to land a useful layer color by eye.

### Phase 2 — Gestures (tap / hold / double-tap)

**Firmware**

- Per-key (and later encoder-press) gesture config, or a small set of custom
  keycodes: `SB_TAP_HOLD`, `SB_DOUBLE_TAP`, etc.
- Timing: tap ≤ T1, hold ≥ T2, double-tap within T3 (constants tunable later).
- On gesture: either
  - send a dedicated HID report (`GESTURE` event), or
  - send distinct ghost keycodes (tap = F13, hold = F14…) — events are cleaner.

**Switchboard**

- Assignment UI: “On tap / On hold / On double-tap” → Action / key / layer.
- Host listens for `GESTURE` and runs the mapped action.

**Done when:** one physical key can mute-on-tap and open-mixer-on-hold without
macros in VIA.

### Phase 3 — Full RGB matrix + press ripple

**Why this is feasible:** the KB16 already is a 16-LED RGB matrix in QMK, not
underglow-only. Phase 1’s “layer wash” is just painting all 16 the same color.
Phase 3 uses the same hardware for per-key motion.

**Press ripple (headline effect)**

When a key goes down, color expands outward from that key through the 4×4 grid
and fades — a stone-in-water feel on the pad.

| Detail | Spec |
|---|---|
| Trigger | Key down on any of the 16 grid keys |
| Origin | LED index for that row/col from `g_led_config` |
| Motion | Neighbor rings by physical distance (`g_led_config.point`) or by Chebyshev grid distance (simpler, looks good on 4×4) |
| Color | Active layer hue/sat (or a distinct “ripple accent” if we add one later) |
| Timing | ~250–400ms expand + fade; tunable |
| Stacking | Overlapping presses allowed (multi-ripple) |
| Base layer | Under the ripple, keep the solid layer wash (ripple is additive / temporary) |

**Implementation path**

1. Prefer a **custom RGB matrix effect** (`rgb_matrix_effect`) that reads the
   QMK key-hit buffer (`g_last_hit_tracker` / `RGB_MATRIX_KEYPRESSES`) and
   paints distance-based brightness each frame.
2. Short-term fallback: enable / tune stock `SOLID_MULTISPLASH` /
   `SOLID_REACTIVE_MULTINEXUS` to validate feel before writing a custom ripple.
3. Expose effect id to Switchboard (`SET_MATRIX_EFFECT`) so the UI can choose
   “solid wash” vs “wash + ripple” per preference (global or per layer later).

**Also in Phase 3**

| Idea | Notes |
|---|---|
| **Per-key accents** | Layer wash + one lit key (last pressed, or “home” key) |
| **Hold breath** | While key held, pulse that key brighter inside the wash |
| **Dictation wash** | Switchboard `SET_LIVE_WASH` while Wispr is active |
| **Layer “breath” idle** | Slow pulse on current layer color when idle |
| **Alert flash** | Host command: flash red 3× (mic muted, error) |
| **Direct `SET_LED`** | Optional host paint for previews / diagnostics |

**Done when:** pressing a key visibly ripples across neighbors on the physical
pad, over the current layer color, without Switchboard running.

### Phase 4 — Deeper pad smarts (optional)

| Idea | Firmware | Still needs Switchboard? |
|---|---|---|
| Combo keys (two keys) | Detect chord on pad | Action map on PC |
| Encoder “modes” by layer | Already natural with layers | — |
| OLED status from host | Draw strings / icons | Sends “Wispr on”, layer name |
| Profile slots | Multiple EEPROM color/gesture banks | Profile picker UI |
| Lock layer | Ignore layer keys until unlock combo | — |
| Sleep / dim | Idle timeout dims LEDs | — |

---

## Other features worth considering now

Brainstorm — not all ship in Phase 1:

1. **Momentary layer with color** — hold layer key → wash switches → release restores (firmware-only once colors live on-pad).
2. **Transparent “PC layer”** — a layer whose keys are mostly Switchboard actions; firmware just reports presses with layer id.
3. **Safe boot wipe** — long-hold combo restores stock keymap defaults if a bad flash of *settings* (not DFU) soft-bricks usability.
4. **Host heartbeat** — if Switchboard disappears, clear live wash overrides.
5. **Dual report** — keep normal keyboard HID for letters; Switchboard channel only for events (no stealing typing).
6. **Knob acceleration / shift** — hold a key while turning encoder for fine vs coarse (gesture + encoder).
7. **Press-and-turn** — encoder press + turn as a distinct gesture.
8. **Per-layer default encoder behavior** — volume on L0, timeline on L1, baked in keymap + optional host actions.

---

## Switchboard impact (after firmware exists)

- New small client for the Switchboard channel (next to `MegalodonPad` VIA).
- Layer color Write path uses `SET_LAYER_COLOR` instead of only global hue/sat.
- HUD / actions can subscribe to `LAYER_CHANGED` and `GESTURE`.
- Assignment dialog gains gesture slots when Phase 2 lands.
- Spec `docs/spec-pad-editing.md` non-goals (tap-dance, per-key RGB) move here as they become in-scope.

---

## Risks

| Risk | Mitigation |
|---|---|
| Vendor tree hard to find / incomplete | Start from closest public KB16; validate matrix + RGB indices early |
| VIA custom space conflicts | Dedicated channel id; proto version field |
| EEPROM wear | Debounce color writes; batch on Write to Pad |
| Gesture timing feels wrong | Expose T1/T2/T3 as get/set once basics work |
| Flash bricks unit | Document bootloader entry; keep stock `.hex` handy |
| PID/VID change breaks Switchboard | Prefer keeping `D010`/`1601`; if not, ship a settings override |

---

## Suggested build order

1. **Phase 0** flash parity with stock  
2. **Phase 1** layer color table + auto-apply + Color Dial + host get/set + Switchboard Write path  
3. **Phase 2** one gesture type (hold) on one key → then double-tap → then UI  
4. **Phase 3** full matrix: press ripple (+ accents / dictation wash)  
5. **Phase 4** only if daily use still wants more  

## Open questions

- [x] Britton’s board: `doio/kb16/rev2` (Maple 003 / `1EAF:0003` — 2026-09-10)
- [x] RGB matrix LED count / map — **16 LEDs, 4×4 key grid** (upstream QMK)
- [ ] Prefer VIA custom channel vs separate raw HID interface?
- [x] Store colors as QMK HSV in EEPROM (matches Color Dial + matrix)
- [ ] Layer wash: force solid while idle, or allow breathing under ripples?
- [ ] Minimum gesture set for v1: hold only, or hold + double-tap together?
- [x] Color Dial: bottom corners = palette + hue; left corners (`R0C0`+`R3C0`) = sat + brightness; same two small knobs
- [ ] Color Dial: exact palette list (~12–16 stops) — draft list OK or trim/add?
- [ ] Color Dial: layer value vs global RGB brightness — keep both, or dial is the only brightness?
- [ ] Ripple: Chebyshev grid rings vs true Euclidean distance from `g_led_config.point`?
- [ ] Ripple default on, or opt-in effect mode?

---

## Success picture (end of Phase 1 + 2)

You assign blue to Layer 0 and amber to Layer 1 in Switchboard, hit Write to Pad,
quit Switchboard. Press Go to Layer 1 on the pad — wash turns amber by itself.
On Layer 1 you hold the bottom corners and turn the top knobs to pick color /
hue, then hold the left corners (top-left + bottom-left) and turn the same
knobs for saturation / brightness — the pad keeps that HSV for Layer 1. A key
set to “tap = mute, hold = open mixer” does the right thing because the pad
tells Switchboard which gesture fired, and Switchboard runs the action.
