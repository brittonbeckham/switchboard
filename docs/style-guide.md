# Switchboard UI Style Guide

Switchboard is dark-mode only — no light theme, no toggle. Every window is
custom-chrome (no native Windows title bar) and modeled after Teams / Slack /
Logi Options+, not stock WinForms. This doc is the reference for keeping new
UI consistent with what's already built.

## Source of truth

All colors, fonts, and metrics live in `UI/Theme.cs`. **Never hardcode a
color or font in a page.** If a screen needs a color Theme.cs doesn't have,
add it there first, then consume it — that keeps a single place to retheme
from.

`UI/Controls.cs` holds custom-drawn replacements for native controls that
can't be restyled: `ToggleSwitch` (replaces `CheckBox`) and `Slider`
(replaces `TrackBar`). Use these instead of the native controls whenever a
page needs an on/off switch or a range control.

## Palette (`Theme.cs`)

| Token | Value | Use |
|---|---|---|
| `Bg` | `#17191D` | Outermost window background |
| `Panel` | `#1E2126` | Window chrome, dialog body, cards' own background |
| `PanelAlt` | `#24272D` | Cards-on-a-panel, input fields, nested editors |
| `Rail` | `#1A1C20` | Nav rail background |
| `LogBg` | `#0F1114` | Log/terminal-style boxes |
| `Ink` | `#EDEFF2` | Primary text |
| `Subtle` | `#9AA1AC` | Secondary text, captions |
| `Faint` | `#5B6270` | Timestamps, disabled text |
| `Line` | `#2C2F35` | Borders, dividers, button outlines |
| `Accent` | `#4C9AFF` | Primary buttons, selected state, links |
| `AccentSoft` | `#1B2A3E` | Accent-tinted fill (selected nav item, selected card) |
| `AccentText` | white | Text/icon drawn on a solid `Accent` fill |
| `Danger` | `#E81123` | Errors, destructive actions |
| `PendingFill`/`PendingBorder`/`PendingText` | amber | Unsaved/pending pad changes |
| `DragMoveBorder` | green | Drag-and-drop move target |
| `DragSwapBorder` | purple | Drag-and-drop swap target |

**Rule of thumb for backgrounds:** each nesting level steps up one shade —
`Bg` → `Panel` → `PanelAlt`. A card sitting directly on the window uses
`Panel`; a control nested inside that card (a text field, a nested editor)
uses `PanelAlt`.

## Typography

All fonts come from `Theme.cs` properties (not `new Font(...)` inline):

| Token | Font | Use |
|---|---|---|
| `Display` | Segoe UI Variable, 14pt bold | Page headline |
| `Title` | Segoe UI Variable, 12.5pt bold | Section headers, dialog titles |
| `Body` | Segoe UI, 9.75pt | Default control text |
| `BodySemibold` | Segoe UI Semibold, 9.75pt | Emphasized body text |
| `Caption` | Segoe UI, 8.5pt | Helper text under a control |
| `CaptionSemibold` | Segoe UI Semibold, 8pt | Small section labels (e.g. "What this does:") |
| `Mono` | Cascadia Mono, 8.75pt | Log viewers, raw keystroke text |

## Metrics

- `RadiusWindow = 12` — outer window corner radius (via `DwmSetWindowAttribute` + `DWMWA_WINDOW_CORNER_PREFERENCE`)
- `RadiusCard = 10` — cards, dialogs, mode panels
- `RadiusControl = 7` — buttons, inputs, chips
- `TitleBarHeight = 40` — custom title bar
- `RailWidth = 200` — nav rail width

## Component patterns

### Window chrome
- `FormBorderStyle.None`, custom title bar (app mark + draggable title + minimize/close buttons).
- Title bar drag: `ReleaseCapture()` + `SendMessage(hWnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero)`.
- Rounded corners: override `OnHandleCreated`, call `DwmSetWindowAttribute` with `DWMWCP_ROUND`.
- Drop shadow: override `CreateParams`, OR in `CS_DROPSHADOW` (`0x00020000`).

### Buttons
- `FlatStyle.Flat` always — native `Button` chrome doesn't theme.
- Primary/CTA: `BackColor = Theme.Accent`, `ForeColor = Color.White`, `FlatAppearance.BorderSize = 0`.
- Secondary: `BackColor = Theme.PanelAlt`, `ForeColor = Theme.Ink`, `FlatAppearance.BorderColor = Theme.Line`.
- Always set `ForeColor` explicitly — never rely on the system default.

### Text inputs
- `BackColor = Theme.PanelAlt` (or `Theme.Panel` if nested one level deeper), `ForeColor = Theme.Ink`, `BorderStyle = FixedSingle`.
- Read-only "this reads as plain text" boxes (summaries, previews): `BorderStyle = None`, `BackColor` matching the *surrounding* panel exactly, so it doesn't look like an input at all.
- Multiline text needs `\r\n`, not `\n`, to actually break a line in a native edit control — always normalize before setting `.Text`.

### Labels
- Always set both `ForeColor` (per role: `Theme.Ink` primary, `Theme.Subtle` captions) AND `BackColor` (matching the parent container) explicitly. `Label`/`Panel` default `BackColor` is a light `SystemColors.Control`-ish gray, not transparent — every container in a dark hierarchy needs this set or you get a light box.

### Cards
- Background `Theme.Panel` on a `Theme.Bg` page (or `Theme.PanelAlt` if the card itself sits on a `Theme.Panel` surface).
- Selected/active state: `Theme.AccentSoft` fill, `Theme.Accent` border.
- Corner radius `RadiusCard`, drawn via `GraphicsPath` + `SmoothingMode.AntiAlias` (WinForms panels don't round natively).

### Toggles and sliders
- Use `ToggleSwitch`/`Slider` from `Controls.cs`, never the native `CheckBox`/`TrackBar`.

### Custom `Control` subclasses
- Every non-trivial public property must carry
  `[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]`
  or the WFO1000 analyzer fails the build. This applies to any property beyond
  what the base `Control` already exposes (e.g. `Checked`, `Value`, `IsSelected`,
  `Target`, `EffectiveLabel`).

### Tab strips

Don't use native `TabControl` in a dark-themed page — even with
`DrawMode = OwnerDrawFixed` and `SetWindowTheme(handle, "", "")`, the
selected tab keeps a native "raised" white overlay Windows draws
independently of owner-draw. It cannot be fully suppressed short of a
WndProc-level rewrite that isn't worth it.

Use the same pattern as the nav rail instead: a `FlowLayoutPanel` of flat
`Button`s (one per tab) plus a set of `Panel`s shown/hidden by visibility
(see `SettingsForm`'s `AddLayerTab`/`SelectLayer`/`ClearLayerTabs` for the
pad's layer tabs, mirroring `AddPage`/`ShowPage` for the main nav). Selected
state: `Theme.AccentSoft` background + `Theme.Accent` text; unselected:
`Theme.PanelAlt` + `Theme.Subtle`.

## Known, accepted gaps

Native scrollbars (e.g. the log viewer) stay light-themed — not worth an
owner-drawn rewrite for that alone.

## What NOT to do

- Don't hardcode `Color.FromArgb(...)` or `Color.White`/`Color.Black` in a page — pull from `Theme`.
- Don't leave a container's `BackColor` unset and assume it'll inherit — it won't.
- Don't use native `CheckBox`/`TrackBar`/`ListBox` chrome as-is without setting `BackColor`/`ForeColor` at minimum (see `ListBox`/`ComboBox` usage in `ActionBuilderForm.cs` for the pattern).
- Don't add a second color/typography system for a "quick" dialog — every window pulls from the same `Theme.cs`.
