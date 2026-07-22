using System.Runtime.InteropServices;

namespace Switchboard.Core;

/// <summary>
/// Global microphone mute via Core Audio endpoint mute — the same mechanism
/// vendor software (Logi Options+) uses: a device-level master mute, so every
/// app capturing from the endpoint receives silence.
/// </summary>
public static class AudioControl
{
    /// <summary>Toggles mute on all active capture endpoints (state taken from the default mic).
    /// Returns the new muted state and how many devices were switched.</summary>
    public static (bool Muted, int Devices) ToggleMicrophoneMute()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleCommunications, out var defaultDevice);
            var defaultVolume = ActivateVolume(defaultDevice);
            defaultVolume.GetMute(out var currentlyMuted);
            var newState = !currentlyMuted;

            enumerator.EnumAudioEndpoints(DataFlowCapture, DeviceStateActive, out var collection);
            collection.GetCount(out var count);
            var switched = 0;
            for (var i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    var context = Guid.Empty;
                    ActivateVolume(device).SetMute(newState, ref context);
                    switched++;
                }
                catch
                {
                    // Some endpoints refuse control; mute the rest anyway.
                }
            }
            return (newState, switched);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Current mute state of the default capture endpoint (null if unavailable).</summary>
    public static bool? IsMicrophoneMuted()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleCommunications, out var device);
            ActivateVolume(device).GetMute(out var muted);
            return muted;
        }
        catch
        {
            return null;
        }
    }

    private static IAudioEndpointVolume ActivateVolume(IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volume);
        return (IAudioEndpointVolume)volume;
    }

    private const int DataFlowCapture = 1;   // eCapture
    private const int RoleCommunications = 2;
    private const int DeviceStateActive = 1;
    private const int ClsCtxAll = 23;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid eventContext);
        void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
