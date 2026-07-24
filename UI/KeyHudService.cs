using Switchboard.Core;
using Switchboard.Util;

namespace Switchboard.UI;

/// <summary>
/// Turns macropad keystrokes (identified by device via RawKeyboardMonitor) into
/// on-screen HUD flashes. Composes held modifiers into chords, and looks up the
/// user's label for whatever the pad emitted. UI thread only.
/// </summary>
internal sealed class KeyHudService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly RawKeyboardMonitor _monitor;
    private readonly KeyHudStack _hud = new();

    // vk → tick of the last down we flashed, so a consumed key's up doesn't double-fire.
    private readonly Dictionary<ushort, long> _shownOnDown = [];

    // (mods, vk) → (labelKey, decoded name), rebuilt from the pad config.
    private Dictionary<(int Mods, ushort Vk), (string LabelKey, string Name)> _lookup = [];

    public KeyHudService(AppSettings settings, RawKeyboardMonitor monitor)
    {
        _settings = settings;
        _monitor = monitor;
        _monitor.DeviceKey += OnPadKey;
        RefreshLookup();
    }

    /// <summary>Re-reads the pad config in the background to keep label lookup current.</summary>
    public void RefreshLookup() => Task.Run(() =>
    {
        try
        {
            var snapshot = MegalodonPad.Read();
            var map = new Dictionary<(int, ushort), (string, string)>();
            for (var l = 0; l < snapshot.LayerCount; l++)
            {
                for (var r = 0; r < snapshot.KeyCodes[l].GetLength(0); r++)
                {
                    for (var c = 0; c < snapshot.KeyCodes[l].GetLength(1); c++)
                        Add(map, snapshot.KeyCodes[l][r, c], $"L{l}K{r},{c}");
                }
                for (var enc = 0; enc < snapshot.EncoderCodes[l].Length; enc++)
                {
                    Add(map, snapshot.EncoderCodes[l][enc].Ccw, $"L{l}E{enc}:ccw");
                    Add(map, snapshot.EncoderCodes[l][enc].Cw, $"L{l}E{enc}:cw");
                }
            }
            _lookup = map;
        }
        catch
        {
            // Pad not reachable — HUD still shows decoded key names without labels.
        }
    });

    private static void Add(Dictionary<(int, ushort), (string, string)> map, ushort code, string labelKey)
    {
        int mods;
        byte basic;
        if (code is >= 0x0100 and <= 0x1FFF) // modifier chord
        {
            mods = (code >> 8) & 0x0F;
            basic = (byte)(code & 0xFF);
        }
        else if (code <= 0xFF)
        {
            mods = 0;
            basic = (byte)code;
        }
        else
        {
            return; // layer/macro codes don't emit a simple VK
        }
        if (KeycodeCatalog.BasicToVk(basic) is ushort vk)
            map.TryAdd((mods, vk), (labelKey, MegalodonPad.KeycodeName(code)));
    }

    private void OnPadKey(ushort vk, bool isDown)
    {
        // Bare modifiers never flash; live state (below) reads them instead — much
        // more reliable than accumulating, whose up-events get eaten by shortcuts.
        if (KeycodeCatalog.ModBitForVk(vk) != 0) return;

        var now = Environment.TickCount64;
        if (isDown)
        {
            _shownOnDown[vk] = now;
            Flash(vk);
        }
        else
        {
            // Key-up: only flash if we never saw the down — the key was consumed by
            // the hotkey system (F24) or another app (WisprFlow). This is the only
            // event those keys deliver to us.
            if (!_shownOnDown.TryGetValue(vk, out var t) || now - t > 1500)
                Flash(vk);
        }
    }

    private void Flash(ushort vk)
    {
        var mods = LiveMods();
        var hasHit = _lookup.TryGetValue((mods, vk), out var hit);

        // This position's pop-up may be individually silenced.
        if (hasHit && _settings.MutedHudKeys.Contains(hit.LabelKey)) return;

        // Ghost keys mapped to a Switchboard action show the action's name.
        if (mods == 0 && vk is >= 0x7C and <= 0x87)
        {
            var fn = vk - 0x7C + 13;
            if (_settings.FunctionKeyActions.TryGetValue($"F{fn}", out var actionId))
            {
                var actionName = ActionCatalog.All.FirstOrDefault(a => a.Id == actionId)?.DisplayName ?? actionId;
                _hud.ShowKey($"F{fn}", actionName, $"Macropad · F{fn}");
                return;
            }
        }

        string cap, title, subtitle;
        if (hasHit)
        {
            var label = _settings.PadLabels.GetValueOrDefault(hit.LabelKey);
            title = label ?? hit.Name;
            subtitle = label != null ? hit.Name : "Megalodon Pad";
            cap = CapText(hit.Name);
        }
        else
        {
            var name = ComposeName(mods, vk);
            title = name;
            subtitle = "Megalodon Pad";
            cap = CapText(name);
        }
        _hud.ShowKey(cap, title, subtitle);
    }

    private static int LiveMods()
    {
        var m = 0;
        if (Held(0x11)) m |= 1; // Ctrl
        if (Held(0x10)) m |= 2; // Shift
        if (Held(0x12)) m |= 4; // Alt
        if (Held(0x5B) || Held(0x5C)) m |= 8; // Win
        return m;
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static string ComposeName(int mods, ushort vk)
    {
        var parts = new List<string>();
        if ((mods & 1) != 0) parts.Add("Ctrl");
        if ((mods & 2) != 0) parts.Add("Shift");
        if ((mods & 4) != 0) parts.Add("Alt");
        if ((mods & 8) != 0) parts.Add("Win");
        parts.Add(VkName(vk));
        return string.Join("+", parts);
    }

    /// <summary>A short glyph for the keycap square: the base key of the chord/name.</summary>
    private static string CapText(string name)
    {
        var baseKey = name.Contains('+') ? name[(name.LastIndexOf('+') + 1)..] : name;
        return baseKey.Length <= 4 ? baseKey : baseKey[..3];
    }

    private static string VkName(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x0D => "Enter",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x20 => "Space",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        _ => $"0x{vk:X2}",
    };

    public void Dispose()
    {
        _monitor.DeviceKey -= OnPadKey;
        _hud.Dispose();
    }
}
