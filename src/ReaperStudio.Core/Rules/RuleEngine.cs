using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReaperStudio.Core.Rules;

/// <summary>Snapshot of the PC state that rules are evaluated against.</summary>
public sealed record RuleContext(DateTime Now, string? FocusedApp, IReadOnlySet<string> RunningApps)
{
    public static RuleContext Capture(bool includeRunning)
    {
        var running = includeRunning
            ? Process.GetProcesses().Select(p => { using (p) return p.ProcessName; })
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new RuleContext(DateTime.Now, ForegroundProcessName(), running);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

public static class RuleEngine
{
    /// <summary>Returns the first enabled matching rule (list order = priority), or null.</summary>
    public static Rule? FirstMatch(IEnumerable<Rule> rules, RuleContext ctx)
    {
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            bool match = r.Trigger switch
            {
                RuleTrigger.Schedule => r.MatchesTime(ctx.Now),
                RuleTrigger.AppFocused => ctx.FocusedApp is { } f && r.Apps.Any(a => Normalize(a).Equals(f, StringComparison.OrdinalIgnoreCase)),
                RuleTrigger.AppRunning => r.Apps.Any(a => ctx.RunningApps.Contains(Normalize(a))),
                _ => false,
            };
            if (match) return r;
        }
        return null;
    }

    public static string Normalize(string app)
    {
        app = app.Trim();
        return app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
    }
}
