using System.Runtime.InteropServices;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// Global hotkeys: Ctrl+Win+Numpad1..9 jump directly to virtual desktop 1..9.
/// Must be created on a thread with a message loop (the UI thread).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkNumpad0 = 0x60;

    private readonly MessageWindow _window;
    private readonly List<int> _registeredIds = [];

    public HotkeyService()
    {
        _window = new MessageWindow(OnHotkey);
        for (var desktop = 1; desktop <= 9; desktop++)
        {
            if (RegisterHotKey(_window.Handle, desktop, ModControl | ModWin | ModNoRepeat, VkNumpad0 + (uint)desktop))
                _registeredIds.Add(desktop);
            else
                Log.Info($"Hotkey Ctrl+Win+Numpad{desktop} is taken by another app; skipped.");
        }
        if (_registeredIds.Count > 0)
            Log.Info($"Hotkeys active: Ctrl+Win+Numpad1-{_registeredIds.Count} → desktop 1-{_registeredIds.Count} (NumLock on).");
    }

    private static void OnHotkey(int desktop)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                VirtualDesktops.SwitchTo(desktop);
            }
            catch (Exception ex)
            {
                Log.Info($"Hotkey desktop {desktop}: {ex.Message}");
            }
        });
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
