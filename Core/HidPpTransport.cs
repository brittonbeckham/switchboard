using HidSharp;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// One open HID handle that speaks Logitech HID++ (short 0x10 / long 0x11 reports).
/// Covers both a Bolt/Unifying receiver endpoint (device indexes 1..7) and a
/// direct Bluetooth LE connection (device index 0xFF).
/// </summary>
public sealed class HidPpTransport : IDisposable
{
    private const byte ReportIdShort = 0x10;
    private const byte ReportIdLong = 0x11;
    private const int LongReportLength = 20;
    private const byte SoftwareId = 0x0A; // our marker in the low nibble of the fn/sw byte
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);

    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly Thread _readerThread;
    private readonly object _requestLock = new(); // one outstanding request at a time
    private volatile bool _disposed;

    private PendingRequest? _pending;

    private sealed record PendingRequest(byte DeviceIndex, byte FeatureIndex, byte FnSw)
    {
        public TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Raised for unsolicited HID++ 2.0 reports: (deviceIndex, featureIndex, fnSw, params).</summary>
    public event Action<byte, byte, byte, byte[]>? EventReceived;

    /// <summary>Raised for HID++ 1.0 receiver notifications such as 0x41 device-connect: (deviceIndex, subId).</summary>
    public event Action<byte, byte>? NotificationReceived;

    /// <summary>Raised once when the underlying HID stream dies (device unplugged, BT dropped).</summary>
    public event Action? Closed;

    public string DevicePath => _device.DevicePath;
    public string Description { get; }

    private HidPpTransport(HidDevice device, HidStream stream)
    {
        _device = device;
        _stream = stream;
        Description = SafeName(device);
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "HidPpReader" };
        _readerThread.Start();
    }

    private static string SafeName(HidDevice device)
    {
        try { return $"{device.GetFriendlyName()} ({device.DevicePath[^12..]})"; }
        catch { return device.DevicePath; }
    }

    /// <summary>
    /// Opens every Logitech HID collection that can carry long HID++ reports.
    /// Vendor collections are the only ones Windows lets us open read/write, so
    /// failures are expected and silently skipped.
    /// </summary>
    public static List<HidPpTransport> OpenAll()
    {
        var transports = new List<HidPpTransport>();
        foreach (var device in DeviceList.Local.GetHidDevices(vendorID: 0x046D))
        {
            try
            {
                if (device.GetMaxInputReportLength() < LongReportLength ||
                    device.GetMaxOutputReportLength() < LongReportLength)
                    continue;
                if (!device.TryOpen(out var stream)) continue;
                stream.ReadTimeout = Timeout.Infinite;
                transports.Add(new HidPpTransport(device, stream));
            }
            catch
            {
                // Not a usable HID++ channel; ignore.
            }
        }
        return transports;
    }

    /// <summary>
    /// Sends a HID++ 2.0 request and waits for the matching response.
    /// Returns the 16 parameter bytes, or null on timeout / HID++ error.
    /// </summary>
    public byte[]? Request(byte deviceIndex, byte featureIndex, byte function, params byte[] parameters) =>
        Request(deviceIndex, featureIndex, function, out _, parameters);

    public byte[]? Request(byte deviceIndex, byte featureIndex, byte function, out string? error, params byte[] parameters)
    {
        error = null;
        if (_disposed) return null;
        var fnSw = (byte)((function << 4) | SoftwareId);
        var report = new byte[LongReportLength];
        report[0] = ReportIdLong;
        report[1] = deviceIndex;
        report[2] = featureIndex;
        report[3] = fnSw;
        Array.Copy(parameters, 0, report, 4, Math.Min(parameters.Length, 16));

        lock (_requestLock)
        {
            var pending = new PendingRequest(deviceIndex, featureIndex, fnSw);
            _pending = pending;
            try
            {
                _stream.Write(report);
                if (pending.Completion.Task.Wait(ResponseTimeout))
                    return pending.Completion.Task.Result;
                error = "timeout";
                return null;
            }
            catch (Exception ex)
            {
                error = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
                return null;
            }
            finally
            {
                _pending = null;
            }
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[Math.Max(_device.GetMaxInputReportLength(), LongReportLength)];
        while (!_disposed)
        {
            int count;
            try
            {
                count = _stream.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                break;
            }
            if (count < 4) continue;
            if (buffer[0] != ReportIdShort && buffer[0] != ReportIdLong) continue;

            var deviceIndex = buffer[1];
            var third = buffer[2];
            var fourth = buffer[3];
            var payload = new byte[16];
            Array.Copy(buffer, 4, payload, 0, Math.Min(count - 4, 16));

            var pending = _pending;

            // HID++ 2.0 error: [id][idx][0xFF][featureIdx][fnSw][errCode]
            if (third == 0xFF && pending != null && deviceIndex == pending.DeviceIndex &&
                fourth == pending.FeatureIndex && count > 4 && buffer[4] == pending.FnSw)
            {
                pending.Completion.TrySetException(new IOException($"HID++ 2.0 error 0x{buffer[5]:X2}"));
                continue;
            }

            // HID++ 1.0 error: [id][idx][0x8F][subId][address][errCode]
            if (third == 0x8F && pending != null && deviceIndex == pending.DeviceIndex)
            {
                pending.Completion.TrySetException(new IOException($"HID++ 1.0 error 0x{(count > 5 ? buffer[5] : 0):X2}"));
                continue;
            }

            // Matched response to our outstanding request.
            if (pending != null && deviceIndex == pending.DeviceIndex &&
                third == pending.FeatureIndex && fourth == pending.FnSw)
            {
                pending.Completion.TrySetResult(payload);
                continue;
            }

            // HID++ 1.0 receiver notifications (0x40 disconnect, 0x41 connect, ...).
            if (third is >= 0x40 and <= 0x7F)
            {
                NotificationReceived?.Invoke(deviceIndex, third);
                continue;
            }

            // Anything else with software id 0 is an unsolicited HID++ 2.0 event.
            if ((fourth & 0x0F) != SoftwareId)
                EventReceived?.Invoke(deviceIndex, third, fourth, payload);
        }

        if (!_disposed) Closed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Completion.TrySetCanceled();
        try { _stream.Dispose(); } catch { }
    }
}
