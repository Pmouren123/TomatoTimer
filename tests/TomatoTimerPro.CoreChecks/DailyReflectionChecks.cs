using System;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Markdown;

public static class DailyReflectionChecks
{
    public static void Run()
    {
        const string daily = "# 每日记录\r\n\r\n今天的总结，必须保留。\r\n";
        var first = new TimerSession { Title = "写作", StartedAt = DateTimeOffset.Now };
        var second = first with { Id = Guid.NewGuid().ToString("N"), Title = "阅读" };
        Check(DailyReflection.Upsert(daily, first, "  ") == daily,
            "An empty optional reflection adds nothing to the daily note");
        var appended = DailyReflection.Upsert(DailyReflection.Upsert(daily, first, "写作收获"), second, "阅读收获");
        Check(appended.StartsWith(daily, StringComparison.Ordinal)
            && DailyReflection.Read(appended, first.Id) == "写作收获"
            && DailyReflection.Read(appended, second.Id) == "阅读收获",
            "Optional entries share one daily note and preserve its original prose");
        var updated = DailyReflection.Upsert(appended, first, "补充收获");
        Check(!updated.Contains("写作收获", StringComparison.Ordinal)
            && DailyReflection.Read(updated, first.Id) == "补充收获"
            && DailyReflection.Upsert(updated, first, "补充收获") == updated,
            "Saving an existing reflection updates its entry without duplicating it");
        var removed = DailyReflection.Remove(updated, first.Id);
        Check(removed.StartsWith(daily, StringComparison.Ordinal)
            && DailyReflection.Read(removed, first.Id) is null
            && DailyReflection.Read(removed, second.Id) == "阅读收获",
            "Removing one reflection preserves daily prose and all other entries");
        Check(DailyReflection.Remove(removed, first.Id) == removed,
            "Repeated removal does not change unrelated daily content");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
