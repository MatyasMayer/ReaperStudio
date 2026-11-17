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
    /// <summary>Trigger priority, highest first. Within one trigger the list order is the priority.</summary>
    private static readonly RuleTrigger[] TriggerPriority = [RuleTrigger.AppFocused, RuleTrigger.AppRunning, RuleTrigger.Schedule];

    /// <summary>Returns the winning enabled rule, or null: app focused beats app running beats schedule;
    /// within each trigger the first matching rule in list order wins.</summary>
    public static Rule? FirstMatch(IEnumerable<Rule> rules, RuleContext ctx)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        foreach (var trigger in TriggerPriority)
            if (enabled.FirstOrDefault(r => r.Trigger == trigger && Matches(r, ctx)) is { } hit)
                return hit;
        return null;
    }

    private static bool Matches(Rule r, RuleContext ctx) => r.Trigger switch
    {
        RuleTrigger.Schedule => r.MatchesTime(ctx.Now),
        RuleTrigger.AppFocused => ctx.FocusedApp is { } f && r.Apps.Any(a => Normalize(a).Equals(f, StringComparison.OrdinalIgnoreCase)),
        RuleTrigger.AppRunning => r.Apps.Any(a => ctx.RunningApps.Contains(Normalize(a))),
        _ => false,
    };

    public static string Normalize(string app)
    {
        app = app.Trim();
        return app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
    }
}
