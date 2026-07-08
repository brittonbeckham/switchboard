using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// Finds the keyboard's Easy-Switch keys over any HID++ channel (Bolt/Unifying
/// receiver slot or direct Bluetooth), diverts them so they stop switching hosts,
/// and turns presses into virtual-desktop switches.
/// </summary>
public sealed class EasySwitchService : IDisposable
{
    // HID++ 2.0 well-known feature ids.
    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureReprogControlsV4 = 0x1B04;

    // Control IDs of the three Easy-Switch (host switch channel) keys.
    private static readonly ushort[] EasySwitchCids = [0x00D1, 0x00D2, 0x00D3];

    private readonly AppSettings _settings;
    private readonly System.Threading.Timer _maintenanceTimer;
    private readonly object _channelsLock = new();
    private readonly List<Channel> _channels = [];
    private volatile bool _disposed;
    private int _scanning; // interlocked flag so scans never overlap

    public event Action? StatusChanged;

    /// <summary>Human-readable summary for the settings dialog.</summary>
    public string Status
    {
        get
        {
            lock (_channelsLock)
            {
                if (_channels.Count == 0) return "Easy-Switch keys not found — is the keyboard connected?";
                return string.Join("; ", _channels.Select(c =>
                    $"Intercepting {c.Cids.Count} Easy-Switch key(s) via {c.Transport.Description}, device index {c.DeviceIndex}"));
            }
        }
    }

    private sealed class Channel
    {
        public required HidPpTransport Transport { get; init; }
        public required byte DeviceIndex { get; init; }
        public required byte FeatureIndex { get; init; }
        public required List<ushort> Cids { get; init; }
        public HashSet<ushort> PressedCids { get; } = [];
    }

    public EasySwitchService(AppSettings settings)
    {
        _settings = settings;
        // Re-apply divert periodically: the setting is volatile on the keyboard and
        // is lost on power-save or host switching, and re-applying is harmless.
        _maintenanceTimer = new System.Threading.Timer(_ => Maintain(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    public void RescanNow() => ThreadPool.QueueUserWorkItem(_ => Maintain());

    private void Maintain()
    {
        if (_disposed || Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            bool anyLive;
            lock (_channelsLock) anyLive = _channels.Count > 0;

            if (anyLive)
                ReapplyDiverts();
            else
                Scan();
        }
        catch (Exception ex)
        {
            Log.Info($"Scan failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    /// <summary>Enumerates all Logitech HID++ channels and adopts any with Easy-Switch keys.</summary>
    private void Scan()
    {
        var transports = HidPpTransport.OpenAll();
        var adoptedAny = false;

        foreach (var transport in transports)
        {
            var adopted = false;
            // 0xFF = the device itself (direct Bluetooth); 1..7 = receiver slots.
            foreach (byte deviceIndex in (byte[])[0xFF, 1, 2, 3, 4, 5, 6, 7])
            {
                if (TryAdopt(transport, deviceIndex))
                {
                    adopted = true;
                    adoptedAny = true;
                    break; // one keyboard per transport is plenty
                }
            }
            if (!adopted) transport.Dispose();
        }

        if (adoptedAny) StatusChanged?.Invoke();
    }

    private bool TryAdopt(HidPpTransport transport, byte deviceIndex)
    {
        // HID++ 2.0 ping: IRoot.getProtocolVersion. Non-connected slots and
        // HID++ 1.0-only endpoints error out or time out.
        var version = transport.Ping(deviceIndex);
        if (version == null || version[0] < 2) return false;

        // Resolve feature 0x1B04 (reprogrammable controls v4).
        var root = transport.Request(deviceIndex, 0x00, 0x0,
            (byte)(FeatureReprogControlsV4 >> 8), (byte)(FeatureReprogControlsV4 & 0xFF));
        if (root == null || root[0] == 0) return false;
        var featureIndex = root[0];

        // Walk the control table looking for the Easy-Switch CIDs.
        var count = transport.Request(deviceIndex, featureIndex, 0x0)?[0] ?? 0;
        var cids = new List<ushort>();
        for (byte i = 0; i < count; i++)
        {
            var info = transport.Request(deviceIndex, featureIndex, 0x1, i);
            if (info == null) continue;
            var cid = (ushort)((info[0] << 8) | info[1]);
            if (EasySwitchCids.Contains(cid)) cids.Add(cid);
            Log.Info($"CID 0x{cid:X4} tid=0x{(info[2] << 8) | info[3]:X4} flags=0x{info[4]:X2}" +
                     $"{((info[4] & 0x20) != 0 ? " DIVERTABLE" : "")} addl=0x{info[8]:X2}");
        }
        if (cids.Count == 0) return false;

        var channel = new Channel
        {
            Transport = transport,
            DeviceIndex = deviceIndex,
            FeatureIndex = featureIndex,
            Cids = cids,
        };

        transport.EventReceived += (devIdx, featIdx, fnSw, payload) => OnEvent(channel, devIdx, featIdx, fnSw, payload);
        transport.NotificationReceived += (devIdx, subId) =>
        {
            // 0x41 = device (re)connected through the receiver: divert is gone, re-apply fast.
            if (subId == 0x41 && devIdx == channel.DeviceIndex)
                ThreadPool.QueueUserWorkItem(_ => Divert(channel));
        };
        transport.Closed += () => OnChannelClosed(channel);

        lock (_channelsLock) _channels.Add(channel);
        Divert(channel);
        Log.Info($"Found Easy-Switch keys ({string.Join(", ", cids.Select(c => $"0x{c:X2}"))}) " +
                 $"on {transport.Description}, device index {deviceIndex}. Diverted.");
        return true;
    }

    private void Divert(Channel channel)
    {
        foreach (var cid in channel.Cids)
        {
            // setCidReporting: cid, flags 0x03 = divert + divert-valid.
            var ok = channel.Transport.Request(channel.DeviceIndex, channel.FeatureIndex, 0x3,
                out var error, (byte)(cid >> 8), (byte)(cid & 0xFF), 0x03) != null;
            if (!ok) Log.Info($"Failed to divert key 0x{cid:X2} ({error}) — will retry.");
        }
    }

    private void ReapplyDiverts()
    {
        List<Channel> channels;
        lock (_channelsLock) channels = [.. _channels];
        foreach (var channel in channels) Divert(channel);
    }

    private void OnChannelClosed(Channel channel)
    {
        lock (_channelsLock) _channels.Remove(channel);
        channel.Transport.Dispose();
        Log.Info($"Lost {channel.Transport.Description} — will rescan.");
        StatusChanged?.Invoke();
        RescanNow();
    }

    private void OnEvent(Channel channel, byte deviceIndex, byte featureIndex, byte fnSw, byte[] payload)
    {
        // divertedButtonsEvent is event 0 of feature 0x1B04: up to four pressed CIDs.
        if (deviceIndex != channel.DeviceIndex || featureIndex != channel.FeatureIndex || (fnSw >> 4) != 0)
            return;

        var nowPressed = new HashSet<ushort>();
        for (var i = 0; i + 1 < 8; i += 2)
        {
            var cid = (ushort)((payload[i] << 8) | payload[i + 1]);
            if (cid != 0) nowPressed.Add(cid);
        }

        foreach (var cid in nowPressed.Except(channel.PressedCids))
            OnKeyDown(cid);

        channel.PressedCids.Clear();
        channel.PressedCids.UnionWith(nowPressed);
    }

    private void OnKeyDown(ushort cid)
    {
        var keyNumber = cid - 0x00D0; // 0xD1..0xD3 -> 1..3
        var desktop = _settings.DesktopForKey(keyNumber);
        Log.Info($"Easy-Switch key {keyNumber} pressed → " +
                 (desktop > 0 ? $"switch to desktop {desktop}" : "no action configured"));
        if (desktop > 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { VirtualDesktops.SwitchTo(desktop); }
                catch (Exception ex) { Log.Info($"Desktop switch failed: {ex.Message}"); }
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _maintenanceTimer.Dispose();
        lock (_channelsLock)
        {
            foreach (var channel in _channels)
            {
                // Best effort: give the keys back their native host-switch behavior.
                foreach (var cid in channel.Cids)
                    channel.Transport.Request(channel.DeviceIndex, channel.FeatureIndex, 0x3,
                        (byte)(cid >> 8), (byte)(cid & 0xFF), 0x02);
                channel.Transport.Dispose();
            }
            _channels.Clear();
        }
    }
}
