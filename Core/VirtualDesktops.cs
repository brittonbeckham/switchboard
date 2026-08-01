using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// Switches Windows virtual desktops by index. Reads the desktop list and current
/// desktop from the registry (stable across Windows 10/11 builds, unlike the
/// undocumented COM interfaces), then replays Ctrl+Win+Left/Right the right
/// number of times.
/// </summary>
public static class VirtualDesktops
{
    private const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    // Every hotkey press runs on its own ThreadPool work item (HotkeyService),
    // so rapid repeated taps used to race each other here: overlapping registry
    // reads and overlapping synthetic Ctrl+Win+Arrow sequences (the modifier
    // "already held" check in SendDesktopArrows even saw a DIFFERENT thread's
    // own in-flight synthetic hold), corrupting the switch count or direction.
    // Serializing everything through one gate makes rapid taps queue up and
    // apply in order instead of interleaving.
    private static readonly Lock Gate = new();

    public static int DesktopCount() => GetDesktopIds().Count;

    /// <summary>Moves the active window to the next virtual desktop (wrapping past
    /// the last back to the first). Stays on the current desktop; only the window
    /// moves.
    ///
    /// The documented IVirtualDesktopManager.MoveWindowToDesktop only succeeds
    /// when the CALLING process owns the target window — verified empirically
    /// (it fails with E_ACCESSDENIED for any other process's window, elevated or
    /// not). Since the whole point here is moving whatever app is in the
    /// foreground — never Switchboard's own window — that path is tried first
    /// (fast, and correct on the rare occasion Switchboard's own window is
    /// foreground) and falls back to the undocumented
    /// IVirtualDesktopManagerInternal.MoveViewToDesktop, which has no such
    /// restriction (the same mechanism VirtualDesktop-manager tools use).</summary>
    public static void MoveActiveWindowToNextDesktop()
    {
        lock (Gate)
        {
            var hwnd = ForegroundStealer.Current;
            if (hwnd == IntPtr.Zero) return;

            var ids = GetDesktopIds();
            if (ids.Count == 0) return;

            var manager = (IVirtualDesktopManager)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidVirtualDesktopManager)!)!;
            if (manager.GetWindowDesktopId(hwnd, out var currentId) != 0)
            {
                Log.Info("Move-to-next-desktop: couldn't read the active window's current desktop.");
                return;
            }

            var currentIndex = ids.IndexOf(currentId);
            if (currentIndex < 0)
            {
                Log.Info("Move-to-next-desktop: active window's desktop isn't in the known desktop list.");
                return;
            }

            var nextIndex = (currentIndex + 1) % ids.Count;
            var nextId = ids[nextIndex];

            if (manager.MoveWindowToDesktop(hwnd, ref nextId) == 0) return; // owns the window — done

            if (!TryMoveViewToDesktop(hwnd, nextId))
                Log.Info("Move-to-next-desktop: both the public and internal move APIs failed.");
        }
    }

    /// <summary>Cross-process window move via the undocumented shell interfaces —
    /// resolves the window to an IApplicationView, then hands that (plus the
    /// target IVirtualDesktop) to IVirtualDesktopManagerInternal.MoveViewToDesktop.
    /// GUIDs/vtable order are Windows-build-specific and sourced from Markus
    /// Scholtes' actively-maintained MScholtes/VirtualDesktop reference; IApplicationView
    /// is IInspectable-derived, which modern .NET's COM interop can't marshal at
    /// all, so it's routed as a raw IntPtr everywhere instead of a typed interface —
    /// fine since we only ever pass it through, never call a method on it.</summary>
    private static bool TryMoveViewToDesktop(IntPtr hwnd, Guid targetDesktopId)
    {
        try
        {
            var shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidImmersiveShell))!;
            var iidViewCollection = typeof(IApplicationViewCollection).GUID;
            var viewCollection = (IApplicationViewCollection)shell.QueryService(ref iidViewCollection, ref iidViewCollection);
            var clsidMgrInternal = ClsidVirtualDesktopManagerInternal;
            var iidMgrInternal = typeof(IVirtualDesktopManagerInternal).GUID;
            var managerInternal = (IVirtualDesktopManagerInternal)shell.QueryService(ref clsidMgrInternal, ref iidMgrInternal);

            if (viewCollection.GetViewForHwnd(hwnd, out var view) != 0 || view == IntPtr.Zero) return false;
            var desktop = managerInternal.FindDesktop(ref targetDesktopId);
            managerInternal.MoveViewToDesktop(view, desktop);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Move-to-next-desktop (internal API fallback): {ex.Message}");
            return false;
        }
    }

    private static readonly Guid ClsidVirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ClsidVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        int GetViews(out IObjectArray array);
        int GetViewsByZOrder(out IObjectArray array);
        int GetViewsByAppUserModelId(string id, out IObjectArray array);
        int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    // Opaque marker for IVirtualDesktop as seen through the internal interface —
    // only ever passed between FindDesktop and MoveViewToDesktop, never called.
    [ComImport]
    [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopInternal;

    [ComImport]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IVirtualDesktopInternal desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IVirtualDesktopInternal GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktopInternal from, int direction, out IVirtualDesktopInternal desktop);
        void SwitchDesktop(IVirtualDesktopInternal desktop);
        void SwitchDesktopAndMoveForegroundView(IVirtualDesktopInternal desktop);
        IVirtualDesktopInternal CreateDesktop();
        void MoveDesktop(IVirtualDesktopInternal desktop, int nIndex);
        void RemoveDesktop(IVirtualDesktopInternal desktop, IVirtualDesktopInternal fallback);
        IVirtualDesktopInternal FindDesktop(ref Guid desktopid);
    }

    /// <summary>Switches to the given 1-based desktop. No-op if already there or out of range.</summary>
    public static void SwitchTo(int desktopNumber)
    {
        lock (Gate) SwitchToLocked(desktopNumber);
    }

    /// <summary>Same as <see cref="SwitchTo"/> but assumes the caller already holds
    /// <see cref="Gate"/> — used internally so a move-then-follow sequence can't be
    /// interleaved by a second, concurrent call.</summary>
    private static void SwitchToLocked(int desktopNumber)
    {
        var ids = GetDesktopIds();
        if (desktopNumber < 1 || desktopNumber > ids.Count)
            throw new InvalidOperationException($"Desktop {desktopNumber} doesn't exist (you have {ids.Count}).");

        var current = GetCurrentDesktopId();
        var currentIndex = current.HasValue ? ids.IndexOf(current.Value) : -1;
        if (currentIndex < 0)
            throw new InvalidOperationException("Couldn't determine the current desktop.");

        var delta = desktopNumber - 1 - currentIndex;
        if (delta == 0) return;
        SendDesktopArrows(delta);
    }

    private static List<Guid> GetDesktopIds()
    {
        using var key = Registry.CurrentUser.OpenSubKey(VdKey);
        if (key?.GetValue("VirtualDesktopIDs") is not byte[] blob || blob.Length < 16)
            return [];
        var ids = new List<Guid>(blob.Length / 16);
        for (var offset = 0; offset + 16 <= blob.Length; offset += 16)
            ids.Add(new Guid(blob.AsSpan(offset, 16)));
        return ids;
    }

    private static Guid? GetCurrentDesktopId()
    {
        // Windows 11 keeps it on the main key; some Windows 10 builds keep it per-session.
        using (var key = Registry.CurrentUser.OpenSubKey(VdKey))
        {
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] blob && blob.Length >= 16)
                return new Guid(blob.AsSpan(0, 16));
        }
        var sessionId = Process.GetCurrentProcess().SessionId;
        using (var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops"))
        {
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] blob && blob.Length >= 16)
                return new Guid(blob.AsSpan(0, 16));
        }
        return null;
    }

    private static void SendDesktopArrows(int delta)
    {
        var arrow = delta > 0 ? VK_RIGHT : VK_LEFT;
        var steps = Math.Abs(delta);

        // The user may be physically holding Ctrl/Win (numpad hotkey). Synthesizing
        // an up for a held modifier would strip it mid-chord and break the next
        // hotkey press, so only touch modifiers that aren't already down.
        var winHeld = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        var ctrlHeld = IsDown(VK_CONTROL);

        if (!winHeld) KeyEvent(VK_LWIN, down: true);
        if (!ctrlHeld) KeyEvent(VK_LCONTROL, down: true);
        for (var i = 0; i < steps; i++)
        {
            KeyEvent(arrow, down: true);
            KeyEvent(arrow, down: false);
            if (i < steps - 1) Thread.Sleep(60); // let the switch animation register each step
        }
        if (!ctrlHeld) KeyEvent(VK_LCONTROL, down: false);
        if (!winHeld) KeyEvent(VK_LWIN, down: false);
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void KeyEvent(ushort vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP },
            },
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput was blocked.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
