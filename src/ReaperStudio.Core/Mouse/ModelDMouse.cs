using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ReaperStudio.Core.Hid;

namespace ReaperStudio.Core.Mouse;

/// <summary>
/// The vendor control collection of the Glorious Model D Wireless (usage page 0xFFFF, usage 0,
/// 65-byte feature report), over the cable or the 2.4 GHz dongle. See docs/mouse/PROTOCOL.md.
/// </summary>
/// <remarks>Every command is answered: write a feature report, wait, read the status back.</remarks>
public sealed class ModelDMouse : IDisposable
{
    public const ushort VendorId = 0x258A, WiredProductId = 0x2012, DongleProductId = 0x2023;
    private const int ReportLength = 65;
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(30), QueryDelay = TimeSpan.FromMilliseconds(50);

    private readonly SafeFileHandle _handle;
    private readonly byte[] _report = new byte[ReportLength];

    public MouseLink Link { get; }
    public string DevicePath { get; }

    private ModelDMouse(SafeFileHandle handle, string path, MouseLink link)
    {
        _handle = handle;
        DevicePath = path;
        Link = link;
    }

    /// <summary>Finds and opens the mouse (cable preferred over dongle), or returns null.</summary>
    public static ModelDMouse? TryOpen()
    {
        foreach (var (pid, link) in new[] { (WiredProductId, MouseLink.Wired), (DongleProductId, MouseLink.Wireless) })
        {
            string match = $"vid_{VendorId:x4}&pid_{pid:x4}";
            foreach (var path in HidNative.EnumerateHidPaths())
            {
                if (!path.Contains(match, StringComparison.OrdinalIgnoreCase)) continue;
                // Probe with no access rights so the mouse's pointer interface is never opened for writing.
                using (var probe = HidNative.CreateFileW(path, 0, HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0))
                {
                    if (probe.IsInvalid || HidNative.GetCaps(probe) is not { UsagePage: 0xFFFF, Usage: 0, FeatureReportByteLength: ReportLength })
                        continue;
                }
                var h = HidNative.CreateFileW(path, HidNative.GenericRead | HidNative.GenericWrite,
                    HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0);
                if (!h.IsInvalid) return new ModelDMouse(h, path, link);
                h.Dispose();
            }
        }
        return null;
    }

    /// <summary>True when the mouse is plugged in by cable (the dongle stays enumerated meanwhile).</summary>
    public static bool IsWiredPresent()
    {
        string match = $"vid_{VendorId:x4}&pid_{WiredProductId:x4}";
        return HidNative.EnumerateHidPaths().Any(p => p.Contains(match, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sends a command and returns the reply status. Retries a few times while the firmware is busy or fails.</summary>
    /// <exception cref="Win32Exception">The device failed, e.g. it was unplugged.</exception>
    public MouseReply Send(byte[] command) => (MouseReply)ExchangeWithRetry(command, WriteDelay)[0];

    /// <summary>Active hardware profile 1..3, or null when the mouse does not answer.</summary>
    /// <exception cref="Win32Exception">The device failed, e.g. it was unplugged.</exception>
    public int? ActiveProfile()
    {
        var r = ExchangeWithRetry(MouseMessages.QueryProfile(), QueryDelay);
        return (MouseReply)r[0] == MouseReply.Ok && r[6] is >= 1 and <= 3 ? r[6] : null;
    }

    /// <summary>Battery percent and charging flag, or null when the mouse does not answer.</summary>
    /// <exception cref="Win32Exception">The device failed, e.g. it was unplugged.</exception>
    public (int Percent, bool Charging)? Battery()
    {
        var r = ExchangeWithRetry(MouseMessages.QueryBattery(), QueryDelay);
        if ((MouseReply)r[0] != MouseReply.Ok || r[5] != 0x83) return null;
        return (Math.Max((int)r[7], 1), r[6] != 0);
    }

    /// <summary>
    /// <see cref="Exchange"/>, repeated up to 3 more times 40 ms apart while the reply is Busy or Failed
    /// (as the vendor app). Returns the last reply, whatever its status.
    /// </summary>
    private byte[] ExchangeWithRetry(byte[] command, TimeSpan delay)
    {
        for (int attempt = 0; ; attempt++)
        {
            var reply = Exchange(command, delay);
            if ((MouseReply)reply[0] is not (MouseReply.Busy or MouseReply.Failed) || attempt == 3) return reply;
            Thread.Sleep(40);
        }
    }

    /// <summary>Writes report 0 + <paramref name="command"/>, waits, reads the reply (report ID stripped).</summary>
    private byte[] Exchange(byte[] command, TimeSpan delay)
    {
        lock (_report)
        {
            Array.Clear(_report);
            command.AsSpan(0, Math.Min(command.Length, ReportLength - 1)).CopyTo(_report.AsSpan(1));
            if (!HidNative.HidD_SetFeature(_handle, _report, ReportLength))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse write failed");
            Thread.Sleep(delay);
            Array.Clear(_report);
            if (!HidNative.HidD_GetFeature(_handle, _report, ReportLength))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse read failed");
            return _report[1..];
        }
    }

    public void Dispose() => _handle.Dispose();
}
