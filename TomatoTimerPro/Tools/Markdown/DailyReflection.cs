using System.Security.Cryptography;
using System.Text;
using TomatoTimerPro.RuntimeStore.States.Timer;

namespace TomatoTimerPro.Tools.Markdown;

/// <summary>Edits one explicitly added reflection while preserving the rest of a daily note.</summary>
public static class DailyReflection
{
    public static string? Read(string dailyContent, string sessionId)
    {
        var markers = Markers(sessionId);
        var range = Find(dailyContent, markers.Start, markers.End);
        if (range is null) return null;
        var content = dailyContent.IndexOf(markers.Content, range.Value.Start, StringComparison.Ordinal);
        if (content < 0 || content >= range.Value.End) return null;
        var offset = content + markers.Content.Length;
        return dailyContent[offset..range.Value.End].Trim('\r', '\n');
    }

    public static string Upsert(string dailyContent, TimerSession session, string reflection)
    {
        if (string.IsNullOrWhiteSpace(reflection)) return Remove(dailyContent, session.Id);
        var markers = Markers(session.Id);
        var title = session.Title.Replace('\r', ' ').Replace('\n', ' ');
        var block = $"{markers.Start}\n## {session.StartedAt.LocalDateTime:HH:mm} · {title}\n\n{markers.Content}\n{reflection.Trim('\r', '\n')}\n{markers.End}";
        var range = Find(dailyContent, markers.Start, markers.End);
        if (range is { } found)
            return dailyContent[..found.Start] + block + dailyContent[(found.End + markers.End.Length)..];
        var separator = dailyContent.Length == 0 || dailyContent.EndsWith("\n\n", StringComparison.Ordinal) ? ""
            : dailyContent.EndsWith('\n') ? "\n" : "\n\n";
        return dailyContent + separator + block + "\n";
    }

    public static string Remove(string dailyContent, string sessionId)
    {
        var markers = Markers(sessionId);
        var range = Find(dailyContent, markers.Start, markers.End);
        if (range is not { } found) return dailyContent;
        var end = found.End + markers.End.Length;
        if (end < dailyContent.Length && dailyContent[end] == '\r') end++;
        if (end < dailyContent.Length && dailyContent[end] == '\n') end++;
        return dailyContent.Remove(found.Start, end - found.Start);
    }

    private static (int Start, int End)? Find(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        return end < 0 ? null : (start, end);
    }

    private static (string Start, string Content, string End) Markers(string sessionId)
    {
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        var prefix = "<!-- TomatoTimerPro:reflection:" + token;
        return (prefix + ":start -->", prefix + ":content -->", prefix + ":end -->");
    }
}
