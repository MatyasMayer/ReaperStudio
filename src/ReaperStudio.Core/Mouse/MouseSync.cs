using System.ComponentModel;
using System.Diagnostics;
using ReaperStudio.Core.Profiles;

namespace ReaperStudio.Core.Mouse;

public sealed record MouseStatus(bool Connected, MouseLink? Link, int? Battery, bool Charging, bool Waiting);

/// <summary>
/// Keeps the mouse in line with the active profile on its own thread, so the slow, acknowledged
/// mouse writes never hold up the keyboard stream.
/// </summary>
/// <remarks>
/// Every effect write is stored in the mouse's memory, so writes are kept rare: only changed
/// commands are sent, a change must be stable for <see cref="Settle"/> (e.g. while dragging a
/// slider), and when the mouse is unreachable (asleep) the write is retried later.
/// </remarks>
public sealed class MouseSync : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);
    private const double ConnectEvery = 2, WiredCheckEvery = 3, BatteryEvery = 60, RetryEvery = 3;

    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile Profile? _profile;
    private volatile bool _enabled = true;
    private volatile bool _dirty;

    private ModelDMouse? _mouse;
    private readonly Dictionary<string, MouseCommand> _sent = [];
    private (int Percent, bool Charging)? _battery;
    private bool _waiting;
    private volatile MouseStatus? _lastStatus;

    /// <summary>Raised on the mouse thread when connection, link, battery or waiting state changes.</summary>
    public event Action<MouseStatus>? StatusChanged;

    /// <summary>Latest status, for listeners that subscribe late.</summary>
    public MouseStatus? Status => _lastStatus;

    /// <summary>Optional diagnostics log, shared with the engine trace.</summary>
    public Action<string>? Trace { get; set; }

    public MouseSync()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ReaperStudio mouse" };
    }

    public void Start() => _thread.Start();

    /// <summary>Profile the mouse should follow; null leaves the mouse as it is. Cheap, call every tick.</summary>
    public void SetProfile(Profile? profile, bool enabled)
    {
        if (!ReferenceEquals(profile, _profile) || enabled != _enabled) _dirty = true;
        _profile = profile;
        _enabled = enabled;
    }

    /// <summary>Call after editing the current profile.</summary>
    public void Invalidate() => _dirty = true;

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Run()
    {
        double nextConnect = 0, nextWiredCheck = 0, nextBattery = 0, nextAttempt = 0, changedAt = 0;
        List<MouseCommand>? pending = null;

        while (!_cts.IsCancellationRequested)
        {
            double now = Now;
            try
            {
                if (_mouse is null && now >= nextConnect)
                {
                    nextConnect = now + ConnectEvery;
                    if (ModelDMouse.TryOpen() is { } m) Connected(m);
                }
                else if (_mouse is { Link: MouseLink.Wireless } && now >= nextWiredCheck)
                {
                    nextWiredCheck = now + WiredCheckEvery;
                    if (ModelDMouse.IsWiredPresent()) Disconnect("cable plugged in"); // reopen on the cable
                }

                if (_mouse is { } mouse)
                {
                    if (now >= nextBattery)
                    {
                        nextBattery = now + BatteryEvery;
                        _battery = mouse.Battery() ?? _battery;
                    }

                    if (_dirty)
                    {
                        _dirty = false;
                        pending = Build(mouse.Link, hwProfile: 1); // profile number filled in when sending
                        changedAt = now;
                    }

                    if (pending is not null && now - changedAt >= Settle.TotalSeconds && now >= nextAttempt)
                    {
                        if (SendChanges(mouse)) { pending = null; _waiting = false; }
                        else { _waiting = true; nextAttempt = now + RetryEvery; }
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
            {
                Disconnect(ex.Message);
                nextConnect = Now + 1;
            }

            Publish();
            _cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
        }
    }

    private void Connected(ModelDMouse mouse)
    {
        _mouse = mouse;
        _sent.Clear(); // the mouse may show anything; send everything once
        _dirty = true;
        _battery = null;
        Trace?.Invoke($"MOUSE CONNECT {mouse.Link}");
    }

    private void Disconnect(string why)
    {
        Trace?.Invoke($"MOUSE DISCONNECT {why}");
        _mouse?.Dispose();
        _mouse = null;
        _waiting = false;
    }

    private List<MouseCommand>? Build(MouseLink link, int hwProfile)
    {
        if (!_enabled || _profile is not { } p || MouseTarget.Resolve(p, link) is not { } target) return null;
        return MouseTarget.Commands(target, hwProfile);
    }

    /// <summary>Sends the commands that differ from what the mouse already has. False: mouse not reachable, retry.</summary>
    private bool SendChanges(ModelDMouse mouse)
    {
        if (mouse.ActiveProfile() is not { } hwProfile) return false;
        var commands = Build(mouse.Link, hwProfile);
        if (commands is null) return true;

        // Switching between whole-mouse and per-zone changes the slot names: send all effect slots then.
        bool layoutChanged = commands.Where(c => c.Slot.StartsWith("fx")).Any(c => !_sent.ContainsKey(c.Slot));
        if (layoutChanged) foreach (var key in _sent.Keys.Where(k => k.StartsWith("fx")).ToList()) _sent.Remove(key);

        foreach (var c in commands)
        {
            if (c.SameAs(_sent.GetValueOrDefault(c.Slot))) continue;
            var reply = mouse.Send(c.Bytes);
            Trace?.Invoke($"MOUSE {c.Slot} {Convert.ToHexString(c.Bytes, 0, 14)} -> {reply}");
            if (reply == MouseReply.NotReachable) return false;
            _sent[c.Slot] = c; // Ok, or a failure we should not hammer the mouse with
        }
        return true;
    }

    private void Publish()
    {
        var status = new MouseStatus(_mouse is not null, _mouse?.Link, _battery?.Percent, _battery?.Charging ?? false, _waiting);
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(1000);
        _mouse?.Dispose();
        _cts.Dispose();
    }
}
