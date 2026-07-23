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
    private readonly KeyHudForm _hud = new();

    private int _heldMods; // our bits: Ctrl 1, Shift 2, Alt 4, Win 8

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
        var modBit = KeycodeCatalog.ModBitForVk(vk);
        if (modBit != 0)
        {
            if (isDown) _heldMods |= modBit;
            else _heldMods &= ~modBit;
            return; // a bare modifier doesn't flash the HUD
        }
        if (!isDown) return;

        var mods = _heldMods;
        string cap, title, subtitle;
        if (_lookup.TryGetValue((mods, vk), out var hit))
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
        _hud.Flash(cap, title, subtitle);
    }

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

    /// <summary>Test hook: flash the HUD with sample content.</summary>
    public void FlashTest() => _hud.Flash("`", "WisprFlow Paste Last", "Ctrl+Shift+`");

    public void Dispose()
    {
        _monitor.DeviceKey -= OnPadKey;
        _hud.Dispose();
    }
}
