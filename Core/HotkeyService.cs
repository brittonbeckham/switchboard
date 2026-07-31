using System.Runtime.InteropServices;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// The single global-hotkey engine. Registers, per settings:
///  - mapped function keys F1–F24 → catalog actions (the key-mapping hub),
///  - Ctrl+Win+Numpad1..9 → desktop jumps (legacy toggle),
///  - the Calculator media key → launch-or-focus (legacy toggle).
/// Must be created on a thread with a message loop; recreate on settings change.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkNumpad0 = 0x60;
    private const uint VkF1 = 0x70; // F1..F24 are 0x70..0x87
    private const uint VkLaunchApp2 = 0xB7;
    private const int CalculatorId = 100;

    // 200 + (n-1)*16 + modBits: n is 1-24, modBits is a 4-bit Ctrl/Shift/Alt/Win
    // mask (0-15) — one unique id per (Fn, modifier combo) pair, 200..583.
    private const int FunctionKeyBaseId = 200;

    private readonly IActionHost _host;
    private readonly MessageWindow _window;
    private readonly List<int> _registeredIds = [];
    private readonly Dictionary<int, string> _actionsByHotkeyId = [];

    public HotkeyService(AppSettings settings, IActionHost host)
    {
        _host = host;
        _window = new MessageWindow(OnHotkey);

        var mappedCount = 0;
        foreach (var (keySpec, actionId) in settings.FunctionKeyActions)
        {
            if (actionId is null or ActionCatalog.None) continue;
            if (!TryParseFunctionKey(keySpec, out var n, out var modBits)) continue;
            var hotkeyId = FunctionKeyBaseId + (n - 1) * 16 + modBits;
            var winModifiers = ModNoRepeat
                | ((modBits & 0x1) != 0 ? ModControl : 0)
                | ((modBits & 0x2) != 0 ? ModShift : 0)
                | ((modBits & 0x4) != 0 ? ModAlt : 0)
                | ((modBits & 0x8) != 0 ? ModWin : 0);
            if (TryRegister(hotkeyId, winModifiers, VkF1 + (uint)(n - 1)))
            {
                _actionsByHotkeyId[hotkeyId] = actionId;
                mappedCount++;
            }
            else
            {
                Log.Info($"Couldn't grab {FormatFunctionKey(n, modBits)} (another app owns it); mapping skipped.");
            }
        }
        if (mappedCount > 0) Log.Info($"{mappedCount} function key(s) mapped to actions.");

        if (settings.NumpadHotkeysEnabled)
        {
            var registered = 0;
            for (var desktop = 1; desktop <= 9; desktop++)
            {
                if (TryRegister(desktop, ModControl | ModWin | ModNoRepeat, VkNumpad0 + (uint)desktop))
                    registered++;
            }
            if (registered > 0)
                Log.Info($"Hotkeys active: Ctrl+Win+Numpad1-{registered} → desktop 1-{registered} (NumLock on).");
        }

        if (settings.CalculatorFocusFixEnabled)
        {
            if (TryRegister(CalculatorId, ModNoRepeat, VkLaunchApp2))
                Log.Info("Calculator key intercepted: will launch or focus Calculator.");
            else
                Log.Info("Couldn't grab the Calculator key (another app owns it).");
        }
    }

    /// <summary>Parses "F17" or a modifier-wrapped form like "Ctrl+Alt+F17".</summary>
    public static bool TryParseFunctionKey(string spec, out int n, out int modBits)
    {
        n = 0;
        modBits = 0;
        var parts = spec.Split('+');
        var fPart = parts[^1];
        if (!(fPart.Length is >= 2 and <= 3 && (fPart[0] == 'F' || fPart[0] == 'f') &&
              int.TryParse(fPart.AsSpan(1), out n) && n is >= 1 and <= 24))
        {
            n = 0;
            return false;
        }
        foreach (var mod in parts[..^1])
            modBits |= mod switch { "Ctrl" => 1, "Shift" => 2, "Alt" => 4, "Win" => 8, _ => 0 };
        return true;
    }

    /// <summary>Canonical settings-key string for a given Fn + modifier mask (Ctrl 1, Shift 2, Alt 4, Win 8).</summary>
    public static string FormatFunctionKey(int n, int modBits)
    {
        var mods = "";
        if ((modBits & 0x1) != 0) mods += "Ctrl+";
        if ((modBits & 0x2) != 0) mods += "Shift+";
        if ((modBits & 0x4) != 0) mods += "Alt+";
        if ((modBits & 0x8) != 0) mods += "Win+";
        return $"{mods}F{n}";
    }

    private bool TryRegister(int id, uint modifiers, uint vk)
    {
        if (!RegisterHotKey(_window.Handle, id, modifiers, vk)) return false;
        _registeredIds.Add(id);
        return true;
    }

    private void OnHotkey(int id)
    {
        Log.Info($"Hotkey fired: {(id == CalculatorId ? "calculator key" :
            _actionsByHotkeyId.TryGetValue(id, out var a) ? $"{DescribeHotkeyId(id)} → {a}" : $"desktop {id}")}");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_actionsByHotkeyId.TryGetValue(id, out var actionId))
                    ActionCatalog.Run(actionId, _host);
                else if (id == CalculatorId)
                    CalculatorLauncher.LaunchOrFocus();
                else if (id is >= 1 and <= 9)
                    VirtualDesktops.SwitchTo(id);
            }
            catch (Exception ex)
            {
                Log.Info($"Hotkey {id}: {ex.Message}");
            }
        });
    }

    private static string DescribeHotkeyId(int id)
    {
        var offset = id - FunctionKeyBaseId;
        return FormatFunctionKey(offset / 16 + 1, offset % 16);
    }

    public void Dispose()
    {
        foreach (var id in _registeredIds) UnregisterHotKey(_window.Handle, id);
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;

        public MessageWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey) _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
