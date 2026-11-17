using System.Text.Json.Serialization;

namespace ReaperStudio.Core.Rules;

public enum RuleTrigger
{
    /// <summary>Active between <see cref="Rule.From"/> and <see cref="Rule.To"/> on the selected days.</summary>
    Schedule,
    /// <summary>Active while one of <see cref="Rule.Apps"/> is the focused window.</summary>
    AppFocused,
    /// <summary>Active while one of <see cref="Rule.Apps"/> is running at all.</summary>
    AppRunning,
}

[Flags]
public enum Days
{
    None = 0,
    Monday = 1, Tuesday = 2, Wednesday = 4, Thursday = 8, Friday = 16, Saturday = 32, Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public RuleTrigger Trigger { get; set; } = RuleTrigger.AppFocused;

    public TimeOnly From { get; set; } = new(22, 0);
    public TimeOnly To { get; set; } = new(7, 0);
    public Days Days { get; set; } = Days.All;

    /// <summary>Process names without ".exe", case-insensitive (e.g. "cs2", "valorant").</summary>
    public List<string> Apps { get; set; } = [];

    public Guid ProfileId { get; set; }

    public static Days ToDays(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => Days.Monday, DayOfWeek.Tuesday => Days.Tuesday, DayOfWeek.Wednesday => Days.Wednesday,
        DayOfWeek.Thursday => Days.Thursday, DayOfWeek.Friday => Days.Friday, DayOfWeek.Saturday => Days.Saturday,
        _ => Days.Sunday,
    };

    /// <summary>True if <paramref name="now"/> falls in the window. Windows may cross midnight (22:00 → 07:00);
    /// the day check then applies to the day the window started.</summary>
    public bool MatchesTime(DateTime now)
    {
        var t = TimeOnly.FromDateTime(now);
        if (From == To) return Days.HasFlag(ToDays(now.DayOfWeek));
        if (From < To) return t >= From && t < To && Days.HasFlag(ToDays(now.DayOfWeek));
        if (t >= From) return Days.HasFlag(ToDays(now.DayOfWeek));
        return t < To && Days.HasFlag(ToDays(now.AddDays(-1).DayOfWeek));
    }

    [JsonIgnore]
    public string Summary => Trigger switch
    {
        RuleTrigger.Schedule => $"{From:HH\\:mm} – {To:HH\\:mm} · {DaysText(Days)}",
        RuleTrigger.AppFocused => Apps.Count == 0 ? "When (no app chosen) is focused" : $"When {string.Join(", ", Apps)} is focused",
        _ => Apps.Count == 0 ? "While (no app chosen) runs" : $"While {string.Join(", ", Apps)} runs",
    };

    public static string DaysText(Days d) => d switch
    {
        Days.All => "every day",
        Days.Weekdays => "weekdays",
        Days.Weekend => "weekends",
        Days.None => "never",
        _ => string.Join(" ", Enum.GetValues<Days>()
            .Where(x => x is not (Days.None or Days.Weekdays or Days.Weekend or Days.All) && d.HasFlag(x))
            .Select(x => x.ToString()[..3])),
    };
}
