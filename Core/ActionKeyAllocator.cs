namespace Switchboard.Core;

/// <summary>
/// Allocates free F13–F24 "ghost" codes for actions — an invisible internal
/// channel the OS and every app ignore, so it's just as safe to layer
/// Ctrl/Shift/Alt/Win on top for far more than 12 slots (12 keys × 16
/// modifier combinations = 192 possible triggers). Callers never need to know
/// which one was picked — just the physical pad position and the action.
/// </summary>
public static class ActionKeyAllocator
{
    /// <summary>Finds a free (raw keycode to write to the pad, FunctionKeyActions
    /// settings key) pair. Null if the entire pool is exhausted. <paramref
    /// name="reservedCodes"/> is every ghost code already staged (but not yet
    /// written) for OTHER positions in the same editing session — without it,
    /// two assignments made before "Write to Pad" would both see the same free
    /// slot and collide on the same ghost key.</summary>
    public static (ushort Code, string KeySpec)? FindFree(
        MegalodonPad.PadSnapshot snapshot, AppSettings settings, IEnumerable<ushort> reservedCodes)
    {
        var usedCodes = new HashSet<ushort>(reservedCodes);
        for (var l = 0; l < snapshot.LayerCount; l++)
        {
            foreach (var code in snapshot.KeyCodes[l]) usedCodes.Add(code);
            foreach (var (ccw, cw) in snapshot.EncoderCodes[l])
            {
                usedCodes.Add(ccw);
                usedCodes.Add(cw);
            }
        }

        // Cycle the plain keys first (F13..F24), then only reach for modifiers
        // once every bare key is spoken for — bare F13 shouldn't burn through
        // all 16 of its modifier variants before F14 is ever tried.
        for (var modBits = 0; modBits <= 0xF; modBits++)
        {
            foreach (var ghost in KeycodeCatalog.GhostKeys)
            {
                KeycodeCatalog.IsGhostKey(ghost, out var fn);
                var keySpec = HotkeyService.FormatFunctionKey(fn, modBits);
                if (settings.FunctionKeyActions.ContainsKey(keySpec)) continue;
                var code = KeycodeCatalog.Chord(modBits, ghost);
                if (usedCodes.Contains(code)) continue;
                return (code, keySpec);
            }
        }
        return null;
    }
}
