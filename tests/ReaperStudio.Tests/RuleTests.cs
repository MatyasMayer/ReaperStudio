using ReaperStudio.Core.Rules;

namespace ReaperStudio.Tests;

public class RuleTests
{
    private static readonly HashSet<string> NoApps = new(StringComparer.OrdinalIgnoreCase);

    // 2026-11-16 is a Monday.
    private static DateTime Mon(int h, int m = 0) => new(2026, 11, 16, h, m, 0);

    [Theory]
    [InlineData(22, 30, true)]
    [InlineData(23, 59, true)]
    [InlineData(6, 59, true)]   // still Monday early morning -> window started Sunday
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void OvernightWindow(int h, int m, bool expected)
    {
        var r = new Rule { Trigger = RuleTrigger.Schedule, From = new(22, 0), To = new(7, 0), Days = Days.All };
        Assert.Equal(expected, r.MatchesTime(Mon(h, m)));
    }

    [Fact]
    public void OvernightWindow_DayBelongsToStartDay()
    {
        // Weekdays only: Friday 23:00 → Saturday 03:00 counts, Sunday 23:00 doesn't, Monday 03:00 (from Sunday) doesn't.
        var r = new Rule { Trigger = RuleTrigger.Schedule, From = new(22, 0), To = new(7, 0), Days = Days.Weekdays };
        Assert.True(r.MatchesTime(new DateTime(2026, 11, 20, 23, 0, 0)));  // Fri
        Assert.True(r.MatchesTime(new DateTime(2026, 11, 21, 3, 0, 0)));   // Sat early (Fri night)
        Assert.False(r.MatchesTime(new DateTime(2026, 11, 22, 23, 0, 0))); // Sun
        Assert.False(r.MatchesTime(Mon(3)));                               // Mon early (Sun night)
    }

    [Fact]
    public void DaytimeWindow()
    {
        var r = new Rule { Trigger = RuleTrigger.Schedule, From = new(9, 0), To = new(17, 0), Days = Days.Monday };
        Assert.True(r.MatchesTime(Mon(9)));
        Assert.False(r.MatchesTime(Mon(17)));
        Assert.False(r.MatchesTime(Mon(10).AddDays(1)));
    }

    [Fact]
    public void FirstMatchingRuleWins_AndDisabledRulesAreSkipped()
    {
        var game = new Rule { Name = "game", Trigger = RuleTrigger.AppFocused, Apps = ["cs2.exe"] };
        var night = new Rule { Name = "night", Trigger = RuleTrigger.Schedule, From = new(0, 0), To = new(0, 0) };
        var ctx = new RuleContext(Mon(23), "CS2", NoApps);

        Assert.Same(game, RuleEngine.FirstMatch([game, night], ctx));
        Assert.Same(night, RuleEngine.FirstMatch([night, game], ctx));
        game.Enabled = false;
        Assert.Same(night, RuleEngine.FirstMatch([game, night], ctx));
    }

    [Fact]
    public void AppRunning_MatchesProcessList()
    {
        var r = new Rule { Trigger = RuleTrigger.AppRunning, Apps = ["Discord"] };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "discord", "explorer" };
        Assert.Same(r, RuleEngine.FirstMatch([r], new RuleContext(Mon(12), "explorer", running)));
        Assert.Null(RuleEngine.FirstMatch([r], new RuleContext(Mon(12), "explorer", NoApps)));
    }
}
