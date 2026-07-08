using System.Diagnostics;
using System.Runtime.InteropServices;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// The single global-hotkey engine. Registers hotkeys according to settings:
/// Ctrl+Win+Numpad1..9 → jump to that virtual desktop, and the keyboard's
/// Calculator key → launch-or-focus Calculator. Must be created on a thread
/// with a message loop (the UI thread); recreate it when settings change.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkNumpad0 = 0x60;
    private const uint VkLaunchApp2 = 0xB7; // the standard "calculator" media key
    private const int CalculatorId = 100;   // hotkey ids 1..9 are desktop jumps

    private readonly MessageWindow _window;
    private readonly List<int> _registeredIds = [];

    public HotkeyService(AppSettings settings)
    {
        _window = new MessageWindow(OnHotkey);

        if (settings.NumpadHotkeysEnabled)
        {
            var registered = 0;
            for (var desktop = 1; desktop <= 9; desktop++)
            {
                if (TryRegister(desktop, ModControl | ModWin | ModNoRepeat, VkNumpad0 + (uint)desktop))
                    registered++;
                else
                    Log.Info($"Hotkey Ctrl+Win+Numpad{desktop} is taken by another app; skipped.");
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

    private bool TryRegister(int id, uint modifiers, uint vk)
    {
        if (!RegisterHotKey(_window.Handle, id, modifiers, vk)) return false;
        _registeredIds.Add(id);
        return true;
    }

    private static void OnHotkey(int id)
    {
        Log.Info($"Hotkey fired: {(id == CalculatorId ? "calculator key" : $"desktop {id}")}");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (id == CalculatorId)
                    LaunchOrFocusCalculator();
                else
                    VirtualDesktops.SwitchTo(id);
            }
            catch (Exception ex)
            {
                Log.Info($"Hotkey {(id == CalculatorId ? "calculator" : $"desktop {id}")}: {ex.Message}");
            }
        });
    }

    private static void LaunchOrFocusCalculator()
    {
        var window = FindCalculatorWindow();
        if (window == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
            // UWP startup: the window appears under ApplicationFrameHost shortly after.
            for (var i = 0; i < 40 && window == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                window = FindCalculatorWindow();
            }
        }
        if (window == IntPtr.Zero)
        {
            Log.Info("Calculator window never appeared.");
            return;
        }

        // Something else may also react to the key (Options+, the shell) and yank
        // the foreground back — keep re-asserting briefly until our focus sticks.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (GetForegroundWindow() != window) FocusWindow(window);
            Thread.Sleep(200);
            if (GetForegroundWindow() == window && attempt >= 2)
            {
                Log.Info("Calculator focused.");
                return;
            }
        }
        Log.Info(GetForegroundWindow() == window
            ? "Calculator focused."
            : "Calculator focus was overridden by another window.");
    }

    /// <summary>
    /// Finds the Calculator window. The modern Calculator is a UWP app: its process
    /// (CalculatorApp.exe) owns no top-level window — the visible frame belongs to
    /// ApplicationFrameHost — so walk frame windows and match the hosted CoreWindow's pid.
    /// </summary>
    private static IntPtr FindCalculatorWindow()
    {
        // Legacy calculators own their window directly.
        foreach (var name in new[] { "win32calc", "Calculator" })
        {
            var direct = Process.GetProcessesByName(name)
                .Select(p => p.MainWindowHandle)
                .FirstOrDefault(h => h != IntPtr.Zero);
            if (direct != IntPtr.Zero) return direct;
        }

        var calcPids = Process.GetProcessesByName("CalculatorApp").Select(p => (uint)p.Id).ToHashSet();
        if (calcPids.Count == 0) return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumWindows((frame, _) =>
        {
            if (GetClassNameOf(frame) != "ApplicationFrameWindow") return true;
            EnumChildWindows(frame, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var pid);
                if (calcPids.Contains(pid))
                {
                    found = frame;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    private static void FocusWindow(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        // Windows only grants SetForegroundWindow to the thread with recent input.
        // Attach to the current foreground thread and tap Alt (the two classic
        // tricks) so the grant applies to us.
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ourThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread &&
                       AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(window);
            BringWindowToTop(window);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        _ = GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
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

    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
