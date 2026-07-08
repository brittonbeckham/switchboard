using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// Diagnostic mode: diverts every divertable key on every Logitech HID++ device
/// and logs every raw HID++ event, so key presses can be observed empirically.
/// Diverts are reverted on dispose. Runs instead of the normal service.
/// </summary>
public sealed class DetectorService : IDisposable
{
    private sealed record DivertedKey(HidPpTransport Transport, byte DeviceIndex, byte FeatureIndex, ushort Cid);

    private readonly List<HidPpTransport> _transports = [];
    private readonly List<DivertedKey> _diverted = [];
    // transport -> (deviceIndex, reprog feature index), for decoding diverted-button events.
    private readonly Dictionary<HidPpTransport, Dictionary<byte, byte>> _reprogIndexes = [];

    public string Status => "KEY DETECTOR running — press keys and watch the log. Stop to restore normal key behavior.";

    public void Start()
    {
        Log.Info("=== Key detector starting: diverting every divertable key on every Logitech device. ===");
        foreach (var transport in HidPpTransport.OpenAll())
        {
            var anyDevice = false;
            transport.EventReceived += (dev, feat, fnSw, payload) => OnRawEvent(transport, dev, feat, fnSw, payload);
            transport.NotificationReceived += (dev, subId) =>
                Log.Info($"NOTIFY {transport.Description} dev=0x{dev:X2} subId=0x{subId:X2} " +
                         (subId == 0x41 ? "(device connected)" : subId == 0x40 ? "(device disconnected)" : ""));
            transport.Closed += () => Log.Info($"CLOSED {transport.Description}");

            // Ask the receiver to forward wireless (connect/disconnect) notifications.
            // HID++ 1.0 set-register 0x00; harmless no-op elsewhere. Try short then long.
            if (transport.RawRequest(0x10, 0xFF, 0x80, 0x00, out _, 0x00, 0x01, 0x00) != null ||
                transport.RawRequest(0x11, 0xFF, 0x80, 0x00, out _, 0x00, 0x01, 0x00) != null)
                Log.Info($"Enabled wireless notifications on {transport.Description}");

            // Direct (Bluetooth) devices answer on 0xFF and ignore the index byte,
            // so don't also probe receiver slots on those transports.
            var isDirect = transport.Ping(0xFF) != null;
            foreach (byte deviceIndex in isDirect ? [0xFF] : (byte[])[1, 2, 3, 4, 5, 6, 7])
            {
                if (ProbeDevice(transport, deviceIndex)) anyDevice = true;
            }

            if (anyDevice || isDirect)
                _transports.Add(transport);
            else
                transport.Dispose();
        }
        Log.Info($"=== Detector armed: {_diverted.Count} key(s) diverted across {_transports.Count} channel(s). Press away. ===");
    }

    private bool ProbeDevice(HidPpTransport transport, byte deviceIndex)
    {
        var version = transport.Ping(deviceIndex);
        if (version == null) return false;
        Log.Info($"--- Device at {transport.Description} index 0x{deviceIndex:X2}, HID++ {version[0]}.{version[1]} ---");

        DumpFeatureTable(transport, deviceIndex);

        // Resolve reprog controls v4 and divert everything divertable.
        var root = transport.Request(deviceIndex, 0x00, 0x0, 0x1B, 0x04);
        if (root == null || root[0] == 0) return true;
        var featureIndex = root[0];
        if (!_reprogIndexes.TryGetValue(transport, out var map))
            _reprogIndexes[transport] = map = [];
        map[deviceIndex] = featureIndex;

        var count = transport.Request(deviceIndex, featureIndex, 0x0)?[0] ?? 0;
        for (byte i = 0; i < count; i++)
        {
            var info = transport.Request(deviceIndex, featureIndex, 0x1, i);
            if (info == null) continue;
            var cid = (ushort)((info[0] << 8) | info[1]);
            var divertable = (info[4] & 0x20) != 0;
            if (!divertable) continue;

            var ok = transport.Request(deviceIndex, featureIndex, 0x3, out var error,
                (byte)(cid >> 8), (byte)(cid & 0xFF), 0x03) != null;
            if (ok)
            {
                _diverted.Add(new DivertedKey(transport, deviceIndex, featureIndex, cid));
                Log.Info($"    diverted CID 0x{cid:X4}");
            }
            else
            {
                Log.Info($"    divert FAILED for CID 0x{cid:X4}: {error}");
            }
        }
        return true;
    }

    private static void DumpFeatureTable(HidPpTransport transport, byte deviceIndex)
    {
        // IFeatureSet (0x0001): getCount, then getFeatureId per index.
        var root = transport.Request(deviceIndex, 0x00, 0x0, 0x00, 0x01);
        if (root == null || root[0] == 0) return;
        var featureSetIndex = root[0];
        var count = transport.Request(deviceIndex, featureSetIndex, 0x0)?[0] ?? 0;
        var features = new List<string>();
        for (byte i = 1; i <= count; i++)
        {
            var entry = transport.Request(deviceIndex, featureSetIndex, 0x1, i);
            if (entry == null) continue;
            features.Add($"0x{(entry[0] << 8) | entry[1]:X4}");
        }
        Log.Info($"    features: {string.Join(" ", features)}");
    }

    private void OnRawEvent(HidPpTransport transport, byte deviceIndex, byte featureIndex, byte fnSw, byte[] payload)
    {
        var hex = Convert.ToHexString(payload);
        var decoded = "";
        if (_reprogIndexes.TryGetValue(transport, out var map) &&
            map.TryGetValue(deviceIndex, out var reprogIndex) && featureIndex == reprogIndex && (fnSw >> 4) == 0)
        {
            var cids = new List<string>();
            for (var i = 0; i + 1 < 8; i += 2)
            {
                var cid = (ushort)((payload[i] << 8) | payload[i + 1]);
                if (cid != 0) cids.Add($"0x{cid:X4}");
            }
            decoded = cids.Count > 0 ? $"  => keys down: {string.Join(", ", cids)}" : "  => all keys up";
        }
        Log.Info($"EVT dev=0x{deviceIndex:X2} feat=0x{featureIndex:X2} fn=0x{fnSw:X2} data={hex}{decoded}");
    }

    public void Dispose()
    {
        foreach (var key in _diverted)
            key.Transport.Request(key.DeviceIndex, key.FeatureIndex, 0x3,
                (byte)(key.Cid >> 8), (byte)(key.Cid & 0xFF), 0x02); // divert off
        foreach (var transport in _transports) transport.Dispose();
        Log.Info("=== Key detector stopped; all keys restored. ===");
    }
}
