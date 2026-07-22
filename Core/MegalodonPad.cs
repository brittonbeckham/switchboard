using HidSharp;

namespace Switchboard.Core;

/// <summary>
/// Reads the DOIO Megalodon (KB16) configuration over its VIA raw-HID channel:
/// layer count, per-layer key matrix, and encoder assignments — decoded into
/// human-readable names.
/// </summary>
public static class MegalodonPad
{
    private const int VendorId = 0xD010;
    private const int ProductId = 0x1601;
    private const int Rows = 5;
    private const int Cols = 5;
    private const int Encoders = 3;

    public sealed record PadSnapshot(
        int LayerCount,
        string[][,] KeyNames,           // [layer][row, col]
        (string Ccw, string Cw)[][] EncoderNames) // [layer][encoder]
    {
        public static readonly string[] EncoderLabels = ["Left knob", "Right knob", "Big knob"];
    }

    /// <summary>Reads the full pad state. Throws if the pad isn't connected.</summary>
    public static PadSnapshot Read()
    {
        var device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
                         .FirstOrDefault(d => d.GetMaxOutputReportLength() == 33)
                     ?? throw new InvalidOperationException("Megalodon not found — is it plugged in?");

        using var stream = device.Open();
        stream.ReadTimeout = 1500;

        var layerCount = Math.Clamp(Command(stream, 0x11)[2], (byte)1, (byte)8);
        var keys = new string[layerCount][,];
        var encoders = new (string, string)[layerCount][];

        for (byte layer = 0; layer < layerCount; layer++)
        {
            keys[layer] = new string[Rows, Cols];
            for (byte row = 0; row < Rows; row++)
            {
                for (byte col = 0; col < Cols; col++)
                {
                    var r = Command(stream, 0x04, layer, row, col);
                    keys[layer][row, col] = KeycodeName((ushort)((r[5] << 8) | r[6]));
                }
            }
            encoders[layer] = new (string, string)[Encoders];
            for (byte enc = 0; enc < Encoders; enc++)
            {
                var ccw = Command(stream, 0x14, layer, enc, 0);
                var cw = Command(stream, 0x14, layer, enc, 1);
                encoders[layer][enc] = (
                    KeycodeName((ushort)((ccw[5] << 8) | ccw[6])),
                    KeycodeName((ushort)((cw[5] << 8) | cw[6])));
            }
        }
        return new PadSnapshot(layerCount, keys, encoders);
    }

    private static byte[] Command(HidStream stream, params byte[] payload)
    {
        var buffer = new byte[33];
        Array.Copy(payload, 0, buffer, 1, payload.Length);
        stream.Write(buffer, 0, buffer.Length);
        var response = new byte[33];
        _ = stream.Read(response, 0, response.Length);
        return response;
    }

    // ---- QMK keycode → friendly name ----

    public static string KeycodeName(ushort keycode)
    {
        switch (keycode)
        {
            case 0x0000: return "—";
            case 0x0001: return "▽ (transparent)";
        }

        // Modifier-wrapped chords: 0x0100-0x1FFF.
        if (keycode is >= 0x0100 and <= 0x1FFF)
            return $"{ModNames((keycode >> 8) & 0x1F)}+{Basic((byte)(keycode & 0xFF))}";
        // Mod-tap: hold = modifier(s), tap = key.
        if (keycode is >= 0x2000 and <= 0x3FFF)
            return $"Tap {Basic((byte)(keycode & 0xFF))} / hold {ModNames((keycode >> 8) & 0x1F)}";
        // Layer-tap: hold = layer, tap = key.
        if (keycode is >= 0x4000 and <= 0x4FFF)
            return $"Tap {Basic((byte)(keycode & 0xFF))} / hold layer {(keycode >> 8) & 0xF}";
        if (keycode is >= 0x5200 and <= 0x521F) return $"Go to layer {keycode - 0x5200}";
        if (keycode is >= 0x5220 and <= 0x523F) return $"Layer {keycode - 0x5220} while held";
        if (keycode is >= 0x5260 and <= 0x527F) return $"Toggle layer {keycode - 0x5260}";
        if (keycode is >= 0x7700 and <= 0x777F) return $"Macro {keycode - 0x7700}";

        return keycode <= 0xFF ? Basic((byte)keycode) : $"0x{keycode:X4}";
    }

    private static string ModNames(int bits)
    {
        var right = (bits & 0x10) != 0;
        var parts = new List<string>();
        if ((bits & 0x01) != 0) parts.Add(right ? "RCtrl" : "Ctrl");
        if ((bits & 0x02) != 0) parts.Add(right ? "RShift" : "Shift");
        if ((bits & 0x04) != 0) parts.Add(right ? "RAlt" : "Alt");
        if ((bits & 0x08) != 0) parts.Add(right ? "RWin" : "Win");
        return string.Join("+", parts);
    }

    private static string Basic(byte code) => code switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + code - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + code - 0x1E)).ToString(),
        0x27 => "0",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x33 => ";",
        0x34 => "'",
        0x35 => "` (backtick)",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "Caps Lock",
        >= 0x3A and <= 0x45 => $"F{code - 0x39}",
        0x46 => "Print Screen",
        0x47 => "Scroll Lock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "Page Up",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "Page Down",
        0x4F => "→",
        0x50 => "←",
        0x51 => "↓",
        0x52 => "↑",
        0x53 => "Num Lock",
        >= 0x54 and <= 0x57 => new[] { "Numpad /", "Numpad *", "Numpad -", "Numpad +" }[code - 0x54],
        0x58 => "Numpad Enter",
        >= 0x59 and <= 0x61 => $"Numpad {code - 0x58}",
        0x62 => "Numpad 0",
        0x63 => "Numpad .",
        0x65 => "Menu",
        >= 0x68 and <= 0x73 => $"F{code - 0x68 + 13} (ghost)",
        0xA5 => "System Power",
        0xA6 => "System Sleep",
        0xA7 => "System Wake",
        0xA8 => "Mute (speakers)",
        0xA9 => "Volume Up",
        0xAA => "Volume Down",
        0xAB => "Next Track",
        0xAC => "Previous Track",
        0xAD => "Media Stop",
        0xAE => "Play / Pause",
        0xAF => "Media Select",
        0xB0 => "Eject",
        0xB1 => "Fast Forward",
        0xB2 => "Rewind",
        0xE0 => "Left Ctrl",
        0xE1 => "Left Shift",
        0xE2 => "Left Alt",
        0xE3 => "Left Win",
        0xE4 => "Right Ctrl",
        0xE5 => "Right Shift",
        0xE6 => "Right Alt",
        0xE7 => "Right Win",
        _ => $"0x{code:X2}",
    };
}
