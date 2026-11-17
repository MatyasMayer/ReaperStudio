using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ReaperStudio.Core.Hid;
using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Core.Keyboard;

/// <summary>
/// The vendor control collection of the CZC Gaming Reaper (usage page 0xFF00, usage 1,
/// 65-byte feature report). See docs/keyboard/PROTOCOL.md.
/// </summary>
/// <remarks>
/// The firmware needs a short pause after each packet. Sent back-to-back, it occasionally fails to
/// acknowledge one, and the write then blocks for the 5 s USB timeout (measured: ~0.3 stalls per 1000
/// packets with no gap, none in 16 500 packets with a 1 ms gap). The vendor app waits 8 ms; we wait 2 ms.
/// </remarks>
public sealed class ReaperKeyboard : IDisposable
{
    public const ushort VendorId = 0x258A, ProductId = 0x00C1;
    private const int ReportLength = 65, PayloadPerPacket = 62;
    private static readonly TimeSpan PacketGap = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(500);

    private readonly SafeFileHandle _handle;
    private readonly byte[] _packet = new byte[ReportLength];
    private readonly PreciseDelay _delay = new();
    private readonly Lock _lock = new();

    public string DevicePath { get; }

    /// <summary>Number of writes that took longer than 500 ms (firmware stalls) since opening.</summary>
    public int Stalls { get; private set; }

    private ReaperKeyboard(SafeFileHandle handle, string path)
    {
        _handle = handle;
        DevicePath = path;
    }

    /// <summary>Finds and opens the keyboard, or returns null when it is not connected.</summary>
    public static ReaperKeyboard? TryOpen()
    {
        string match = $"vid_{VendorId:x4}&pid_{ProductId:x4}";
        foreach (var path in HidNative.EnumerateHidPaths())
        {
            if (!path.Contains(match, StringComparison.OrdinalIgnoreCase)) continue;
            // Query capabilities with a handle that has no access rights, so the keyboard's typing
            // interfaces are never opened for writing.
            using (var probe = HidNative.CreateFileW(path, 0, HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0))
            {
                if (probe.IsInvalid || HidNative.GetCaps(probe) is not { UsagePage: 0xFF00, Usage: 1, FeatureReportByteLength: ReportLength })
                    continue;
            }
            var h = HidNative.CreateFileW(path, HidNative.GenericRead | HidNative.GenericWrite,
                HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0);
            if (!h.IsInvalid) return new ReaperKeyboard(h, path);
            h.Dispose();
        }
        return null;
    }

    /// <summary>Sends one logical message, split into 0x0A feature reports with a short gap after each.</summary>
    /// <exception cref="Win32Exception">The device failed, e.g. it was unplugged.</exception>
    public void Send(ReadOnlySpan<byte> message)
    {
        int count = (message.Length + PayloadPerPacket - 1) / PayloadPerPacket;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                Array.Clear(_packet);
                _packet[0] = 0x0A;
                _packet[1] = (byte)count;
                _packet[2] = (byte)(i + 1);
                var chunk = message.Slice(i * PayloadPerPacket, Math.Min(PayloadPerPacket, message.Length - i * PayloadPerPacket));
                chunk.CopyTo(_packet.AsSpan(3));

                long start = Stopwatch.GetTimestamp();
                if (!HidNative.HidD_SetFeature(_handle, _packet, ReportLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard write failed");
                if (Stopwatch.GetElapsedTime(start) > StallThreshold) Stalls++;
                _delay.Wait(PacketGap);
            }
        }
    }

    public void SetEffect(HardwareEffect effect) => Send(Messages.Effect(effect));
    public void SendFrame(LedFrame frame) => Send(Messages.RealtimeFrame(frame));
    public void StoreCustomColors(int slot, LedFrame frame) => Send(Messages.CustomColors(slot, frame));

    public void Dispose()
    {
        _handle.Dispose();
        _delay.Dispose();
    }
}
