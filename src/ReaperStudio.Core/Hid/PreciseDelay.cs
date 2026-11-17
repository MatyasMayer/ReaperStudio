using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReaperStudio.Core.Hid;

/// <summary>
/// Millisecond-accurate sleep. Thread.Sleep follows the ~15.6 ms system timer tick, which is far too
/// coarse for the 2 ms gap the keyboard needs between packets. Uses a high-resolution waitable timer
/// (Windows 10 1803+), falling back to a short spin.
/// </summary>
internal sealed class PreciseDelay : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    private readonly nint _timer;

    public PreciseDelay()
    {
        _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    public void Wait(TimeSpan duration)
    {
        if (_timer != 0)
        {
            long due = -duration.Ticks; // negative = relative, in 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
            {
                WaitForSingleObject(_timer, uint.MaxValue);
                return;
            }
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration) Thread.SpinWait(50);
    }

    public void Dispose()
    {
        if (_timer != 0) CloseHandle(_timer);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completion, nint arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
