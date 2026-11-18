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
/// slider), and when the mouse is unreachable (asleep) or refuses a command the write is retried later.
/// </remarks>
public sealed class MouseSync : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400), Tick = TimeSpan.FromMilliseconds(100);
    private const double ConnectEvery = 2, LinkCheckEvery = 3, BatteryEvery = 60, RetryEvery = 3, MaxRetryEvery = 60, ErrorBackoff = 5;

    /// <summary>Outcome of one attempt to bring the mouse up to date.</summary>
    public enum SendResult
    {
        /// <summary>The mouse accepted every command that differed.</summary>
        Done,
        /// <summary>The mouse could not be reached (asleep); nothing more was sent.</summary>
        NotReachable,
        /// <summary>Some commands were refused (failed or unsupported); they stay pending.</summary>
        Incomplete,
    }

    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile Profile? _profile;
    private volatile bool _enabled = true;
    private int _dirty; // 0 or 1; set and consumed with Interlocked so no change is lost

    private ModelDMouse? _mouse;
    private readonly Dictionary<string, MouseCommand> _sent = [];
    private bool _controlling; // the last seen target was a real look, not "leave the mouse alone"
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
    /// <remarks>
    /// The new values are stored before the dirty flag is raised, so when the mouse thread consumes
    /// the flag it reads this profile, not the previous one.
    /// </remarks>
    public void SetProfile(Profile? profile, bool enabled)
    {
        bool changed = !ReferenceEquals(profile, _profile) || enabled != _enabled;
        _profile = profile;
        _enabled = enabled;
        if (changed) MarkDirty();
    }

    /// <summary>Call after editing the current profile.</summary>
    public void Invalidate() => MarkDirty();

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Clears the dirty flag; true when it was set. A full fence, so the profile read afterwards is current.</summary>
    private bool TakeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Run()
    {
        double nextConnect = 0, nextLinkCheck = 0, nextBattery = 0, nextAttempt = 0, changedAt = 0;
        bool pending = false;
        int failedAttempts = 0;

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
                else if (_mouse is not null && now >= nextLinkCheck)
                {
                    nextLinkCheck = now + LinkCheckEvery;
                    CheckLink(_mouse);
                }

                if (_mouse is { } mouse)
                {
                    if (now >= nextBattery)
                    {
                        nextBattery = now + BatteryEvery;
                        _battery = mouse.Battery() ?? _battery;
                    }

                    if (TakeDirty())
                    {
                        pending = UpdateControlling(mouse.Link);
                        changedAt = now;
                        if (!pending) _waiting = false; // nothing left to wait for
                        if (failedAttempts > 0) { failedAttempts = 0; nextAttempt = 0; } // a new look gets a prompt try
                    }

                    if (pending && now - changedAt >= Settle.TotalSeconds && now >= nextAttempt)
                    {
                        var result = SendChanges(mouse);
                        _waiting = result == SendResult.NotReachable;
                        if (result == SendResult.Done) { pending = false; failedAttempts = 0; }
                        else if (result == SendResult.NotReachable) nextAttempt = now + RetryEvery;
                        else nextAttempt = now + RetryDelay(++failedAttempts);
                    }
                }

                Publish();
            }
            catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
            {
                Disconnect(ex.Message);
                nextConnect = Now + 1;
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break; // shutting down: just leave
                Log($"MOUSE ERROR {ex}");
                Disconnect("unexpected error");
                nextConnect = Now + ErrorBackoff;
            }

            _cts.Token.WaitHandle.WaitOne(Tick);
        }
    }

    /// <summary>Back-off after refused commands: <see cref="RetryEvery"/>, doubling up to <see cref="MaxRetryEvery"/>.</summary>
    private static double RetryDelay(int failedAttempts) =>
        Math.Min(RetryEvery * Math.Pow(2, Math.Min(failedAttempts - 1, 10)), MaxRetryEvery);

    /// <summary>Drops the connection when the link changed, so the next connect picks the right one.</summary>
    private void CheckLink(ModelDMouse mouse)
    {
        bool wired = ModelDMouse.IsWiredPresent();
        if (mouse.Link == MouseLink.Wireless && wired) Disconnect("cable plugged in"); // reopen on the cable
        else if (mouse.Link == MouseLink.Wired && !wired) Disconnect("cable unplugged"); // reopen on the dongle
    }

    /// <summary>
    /// Records whether the current profile wants to control the mouse; true when it does. Coming back
    /// from "leave the mouse alone" forgets what was sent, since the mouse may have been changed meanwhile.
    /// </summary>
    private bool UpdateControlling(MouseLink link)
    {
        bool controlling = Target(link) is not null;
        if (controlling && !_controlling) _sent.Clear();
        _controlling = controlling;
        return controlling;
    }

    private void Connected(ModelDMouse mouse)
    {
        _mouse = mouse;
        _sent.Clear(); // the mouse may show anything; send everything once
        _controlling = false;
        MarkDirty();
        _battery = null;
        Log($"MOUSE CONNECT {mouse.Link}");
    }

    private void Disconnect(string why)
    {
        Log($"MOUSE DISCONNECT {why}");
        _mouse?.Dispose();
        _mouse = null;
        _waiting = false;
    }

    private MouseTargetLook? Target(MouseLink link) =>
        _enabled && _profile is { } p ? MouseTarget.Resolve(p, link) : null;

    /// <summary>Sends the commands that differ from what the mouse already has.</summary>
    private SendResult SendChanges(ModelDMouse mouse)
    {
        if (mouse.ActiveProfile() is not { } hwProfile) return SendResult.NotReachable;
        if (Target(mouse.Link) is not { } target) return SendResult.Done;

        return SendChanges(_sent, MouseTarget.Commands(target, hwProfile), c =>
        {
            var reply = mouse.Send(c.Bytes);
            Log($"MOUSE {c.Slot} {Convert.ToHexString(c.Bytes, 0, 14)} -> {reply}");
            return reply;
        });
    }

    /// <summary>
    /// Sends, through <paramref name="send"/>, each command in <paramref name="commands"/> that differs
    /// from <paramref name="sent"/>, and records in <paramref name="sent"/> only the ones the mouse
    /// answered <see cref="MouseReply.Ok"/>. Stops at the first <see cref="MouseReply.NotReachable"/>.
    /// </summary>
    /// <remarks>
    /// Switching between whole-mouse ("fx") and per-zone ("fx0".."fx2") changes the effect slot names,
    /// and either layout overwrites the other on the mouse: when <paramref name="sent"/> holds effect
    /// slots of the other layout, all effect slots are forgotten so the whole new layout is sent.
    /// </remarks>
    public static SendResult SendChanges(Dictionary<string, MouseCommand> sent, IReadOnlyList<MouseCommand> commands,
        Func<MouseCommand, MouseReply> send)
    {
        var slots = commands.Select(c => c.Slot).ToHashSet();
        if (sent.Keys.Any(k => IsEffectSlot(k) && !slots.Contains(k)))
            foreach (var key in sent.Keys.Where(IsEffectSlot).ToList()) sent.Remove(key);

        var result = SendResult.Done;
        foreach (var c in commands)
        {
            if (c.SameAs(sent.GetValueOrDefault(c.Slot))) continue;
            var reply = send(c);
            if (reply == MouseReply.NotReachable) return SendResult.NotReachable;
            if (reply == MouseReply.Ok) sent[c.Slot] = c;
            else result = SendResult.Incomplete; // refused: retry later, keep sending the rest
        }
        return result;
    }

    private static bool IsEffectSlot(string slot) => slot.StartsWith("fx", StringComparison.Ordinal);

    private void Publish()
    {
        var status = new MouseStatus(_mouse is not null, _mouse?.Link, _battery?.Percent, _battery?.Charging ?? false, _waiting);
        if (status == _lastStatus) return;
        _lastStatus = status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { Log($"MOUSE STATUS LISTENER ERROR {ex}"); }
    }

    /// <summary>Writes to <see cref="Trace"/>; a failing log never stops the mouse thread.</summary>
    private void Log(string line)
    {
        try { Trace?.Invoke(line); }
        catch (Exception) { /* diagnostics only */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        bool stopped = !_thread.IsAlive || _thread.Join(1000);
        _mouse?.Dispose();
        if (stopped) _cts.Dispose(); // a thread still stuck in I/O keeps waiting on its token
    }
}
