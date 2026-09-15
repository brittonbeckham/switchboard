using HidSharp;
using Switchboard.Util;

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
        string[][,] KeyNames,                     // [layer][row, col]
        ushort[][,] KeyCodes,                     // raw QMK keycodes
        (string Ccw, string Cw)[][] EncoderNames, // [layer][encoder]
        (ushort Ccw, ushort Cw)[][] EncoderCodes)
    {
        public static readonly string[] EncoderLabels = ["Left Knob", "Right Knob", "Big Knob"];
    }

    /// <summary>Reads the full pad state. Throws if the pad isn't connected.</summary>
    public static PadSnapshot Read()
    {
        using var stream = OpenStream();

        var layerCount = Math.Clamp(Command(stream, 0x11)[2], (byte)1, (byte)8);
        var names = new string[layerCount][,];
        var codes = new ushort[layerCount][,];
        var encoderNames = new (string, string)[layerCount][];
        var encoderCodes = new (ushort, ushort)[layerCount][];

        for (byte layer = 0; layer < layerCount; layer++)
        {
            names[layer] = new string[Rows, Cols];
            codes[layer] = new ushort[Rows, Cols];
            for (byte row = 0; row < Rows; row++)
            {
                for (byte col = 0; col < Cols; col++)
                {
                    var r = Command(stream, 0x04, layer, row, col);
                    var code = (ushort)((r[5] << 8) | r[6]);
                    codes[layer][row, col] = code;
                    names[layer][row, col] = KeycodeName(code);
                }
            }
            encoderNames[layer] = new (string, string)[Encoders];
            encoderCodes[layer] = new (ushort, ushort)[Encoders];
            for (byte enc = 0; enc < Encoders; enc++)
            {
                var ccw = Command(stream, 0x14, layer, enc, 0);
                var cw = Command(stream, 0x14, layer, enc, 1);
                var ccwCode = (ushort)((ccw[5] << 8) | ccw[6]);
                var cwCode = (ushort)((cw[5] << 8) | cw[6]);
                encoderCodes[layer][enc] = (ccwCode, cwCode);
                encoderNames[layer][enc] = (KeycodeName(ccwCode), KeycodeName(cwCode));
            }
        }
        return new PadSnapshot(layerCount, names, codes, encoderNames, encoderCodes);
    }

    /// <summary>Writes one key position and verifies by read-back. Returns success.</summary>
    public static bool WriteKey(int layer, int row, int col, ushort code)
    {
        using var stream = OpenStream();
        Command(stream, 0x05, (byte)layer, (byte)row, (byte)col, (byte)(code >> 8), (byte)(code & 0xFF));
        var r = Command(stream, 0x04, (byte)layer, (byte)row, (byte)col);
        return ((r[5] << 8) | r[6]) == code;
    }

    /// <summary>Writes one encoder direction and verifies by read-back. Returns success.</summary>
    public static bool WriteEncoder(int layer, int encoder, bool clockwise, ushort code)
    {
        using var stream = OpenStream();
        Command(stream, 0x15, (byte)layer, (byte)encoder, (byte)(clockwise ? 1 : 0),
            (byte)(code >> 8), (byte)(code & 0xFF));
        var r = Command(stream, 0x14, (byte)layer, (byte)encoder, (byte)(clockwise ? 1 : 0));
        return ((r[5] << 8) | r[6]) == code;
    }

    // ---- Backup & restore ----

    private const int MaxBackups = 15;

    public static string BackupDirectory =>
        Path.Combine(AppSettings.Directory, "pad-backups");

    /// <summary>Reads the global RGB-matrix lighting: [brightness, effect, speed, hue, sat]. Null if unavailable.</summary>
    public static int[]? ReadLighting()
    {
        try
        {
            using var stream = OpenStream();
            int Value(byte id, int offset) => Command(stream, 0x08, 0x03, id)[4 + offset];
            var color = Command(stream, 0x08, 0x03, 4);
            return [Value(1, 0), Value(2, 0), Value(3, 0), color[4], color[5]];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the pad's global RGB-matrix hue/saturation (QMK 0–255). Keeps the
    /// current brightness/effect/speed. When <paramref name="saveToEeprom"/> is
    /// true, persists so it survives unplug.
    /// </summary>
    public static void WriteLightingColor(Color color, bool saveToEeprom = true)
    {
        ColorToHueSat(color, out var hue, out var sat);
        using var stream = OpenStream();
        Command(stream, 0x07, 0x03, 4, hue, sat);
        if (saveToEeprom) Command(stream, 0x09);
    }

    /// <summary>Converts an RGB color to QMK RGB-matrix hue/sat bytes (0–255).</summary>
    public static void ColorToHueSat(Color color, out byte hue, out byte sat)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        float h;
        if (delta < 0.00001f) h = 0;
        else if (Math.Abs(max - r) < 0.00001f) h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
        else if (Math.Abs(max - g) < 0.00001f) h = ((b - r) / delta + 2f) / 6f;
        else h = ((r - g) / delta + 4f) / 6f;

        var s = max < 0.00001f ? 0f : delta / max;
        hue = (byte)Math.Clamp((int)Math.Round(h * 255f), 0, 255);
        sat = (byte)Math.Clamp((int)Math.Round(s * 255f), 0, 255);
    }

    /// <summary>Serializes a snapshot (+ optional lighting + Switchboard host bindings) to backup JSON.</summary>
    private static string Serialize(PadSnapshot snapshot, int[]? lighting, AppSettings host)
    {
        var layers = new List<object>();
        for (var l = 0; l < snapshot.LayerCount; l++)
        {
            var keyRows = new List<ushort[]>();
            for (var r = 0; r < Rows; r++)
            {
                var rowCodes = new ushort[Cols];
                for (var c = 0; c < Cols; c++) rowCodes[c] = snapshot.KeyCodes[l][r, c];
                keyRows.Add(rowCodes);
            }
            layers.Add(new
            {
                Keys = keyRows,
                Encoders = snapshot.EncoderCodes[l].Select(e => new[] { e.Ccw, e.Cw }).ToList(),
            });
        }
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Layers = layers,
            Lighting = lighting,
            // Switchboard-side state that makes ghost keys (F13–F24) and labels work.
            FunctionKeyActions = host.FunctionKeyActions,
            PadLabels = host.PadLabels,
            PadLayerColors = host.PadLayerColors,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Saves a rolling, timestamped backup (keys + encoders + lighting + Switchboard
    /// action bindings / labels / layer colors) — but only if the content differs from
    /// the newest existing backup. Keeps the newest 15. Returns the path if a new
    /// backup was written, else null.
    /// </summary>
    public static string? SaveBackupIfChanged(PadSnapshot snapshot, int[]? lighting, AppSettings? host = null)
    {
        Directory.CreateDirectory(BackupDirectory);
        host ??= AppSettings.Load();
        var json = Serialize(snapshot, lighting, host);
        var latest = Directory.GetFiles(BackupDirectory, "pad-*.json")
            .OrderByDescending(f => f).FirstOrDefault();
        if (latest != null && File.ReadAllText(latest) == json) return null; // unchanged

        var path = Path.Combine(BackupDirectory, $"pad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, json);
        foreach (var old in Directory.GetFiles(BackupDirectory, "pad-*.json")
                     .OrderByDescending(f => f).Skip(MaxBackups))
        {
            try { File.Delete(old); } catch { /* best effort */ }
        }
        return path;
    }

    /// <summary>
    /// Writes every position (and lighting, if present) from a backup file back to the
    /// pad. When <paramref name="host"/> is provided and the backup includes Switchboard
    /// bindings, also restores FunctionKeyActions / PadLabels / PadLayerColors and saves.
    /// Returns mismatch count for pad keycodes.
    /// </summary>
    public static int RestoreBackup(string path, AppSettings? host = null)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var mismatches = 0;
        using var stream = OpenStream();
        var layers = doc.RootElement.GetProperty("Layers");
        for (var l = 0; l < layers.GetArrayLength(); l++)
        {
            var keys = layers[l].GetProperty("Keys");
            for (var r = 0; r < keys.GetArrayLength(); r++)
            {
                var rowCodes = keys[r];
                for (var c = 0; c < rowCodes.GetArrayLength(); c++)
                {
                    var code = rowCodes[c].GetUInt16();
                    Command(stream, 0x05, (byte)l, (byte)r, (byte)c, (byte)(code >> 8), (byte)(code & 0xFF));
                    var back = Command(stream, 0x04, (byte)l, (byte)r, (byte)c);
                    if (((back[5] << 8) | back[6]) != code) mismatches++;
                }
            }
            var encoders = layers[l].GetProperty("Encoders");
            for (var e = 0; e < encoders.GetArrayLength(); e++)
            {
                for (var dir = 0; dir < 2; dir++)
                {
                    var code = encoders[e][dir].GetUInt16();
                    Command(stream, 0x15, (byte)l, (byte)e, (byte)dir, (byte)(code >> 8), (byte)(code & 0xFF));
                    var back = Command(stream, 0x14, (byte)l, (byte)e, (byte)dir);
                    if (((back[5] << 8) | back[6]) != code) mismatches++;
                }
            }
        }

        // Lighting (brightness, effect, speed, hue, sat) via the RGB-matrix custom channel.
        if (doc.RootElement.TryGetProperty("Lighting", out var lp) &&
            lp.ValueKind == System.Text.Json.JsonValueKind.Array && lp.GetArrayLength() == 5)
        {
            var v = new byte[5];
            for (var i = 0; i < 5; i++) v[i] = (byte)lp[i].GetInt32();
            Command(stream, 0x07, 0x03, 1, v[0]);       // brightness
            Command(stream, 0x07, 0x03, 2, v[1]);       // effect
            Command(stream, 0x07, 0x03, 3, v[2]);       // speed
            Command(stream, 0x07, 0x03, 4, v[3], v[4]); // color (hue, sat)
            Command(stream, 0x09);                      // save lighting to EEPROM
        }

        if (host != null)
            ApplyHostSideFromBackup(doc.RootElement, host);

        return mismatches;
    }

    /// <summary>Copies FunctionKeyActions / PadLabels / PadLayerColors from a backup
    /// into <paramref name="host"/> when those fields exist (newer backups). Older
    /// keypad-only backups are left alone on the host side.</summary>
    public static bool ApplyHostSideFromBackup(System.Text.Json.JsonElement root, AppSettings host)
    {
        var changed = false;
        if (TryReadStringMap(root, "FunctionKeyActions", out var actions))
        {
            host.FunctionKeyActions = actions;
            changed = true;
        }
        if (TryReadStringMap(root, "PadLabels", out var labels))
        {
            host.PadLabels = labels;
            changed = true;
        }
        if (TryReadStringMap(root, "PadLayerColors", out var colors))
        {
            host.PadLayerColors = colors;
            changed = true;
        }
        if (changed)
        {
            host.Save();
            Log.Info($"Restored Switchboard pad bindings from backup " +
                     $"({host.FunctionKeyActions.Count} action(s), {host.PadLabels.Count} label(s)).");
        }
        return changed;
    }

    private static bool TryReadStringMap(System.Text.Json.JsonElement root, string name, out Dictionary<string, string> map)
    {
        map = [];
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                map[prop.Name] = prop.Value.GetString() ?? "";
        }
        return true;
    }

    private static HidStream OpenStream()
    {
        var device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
                         .FirstOrDefault(d => d.GetMaxOutputReportLength() == 33)
                     ?? throw new InvalidOperationException("Megalodon not found — is it plugged in?");
        var stream = device.Open();
        stream.ReadTimeout = 1500;
        return stream;
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

    private static readonly Dictionary<string, byte> BasicByName = BuildBasicByName();

    /// <summary>Reverse of <see cref="Basic"/>: the raw byte for a basic key's display name, if any.</summary>
    public static byte? BasicCodeFromName(string name) =>
        BasicByName.TryGetValue(name, out var code) ? code : null;

    private static Dictionary<string, byte> BuildBasicByName()
    {
        var map = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (var c = 0; c <= 0xFF; c++)
        {
            var name = Basic((byte)c);
            if (!name.StartsWith("0x", StringComparison.Ordinal)) map[name] = (byte)c;
        }
        return map;
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
