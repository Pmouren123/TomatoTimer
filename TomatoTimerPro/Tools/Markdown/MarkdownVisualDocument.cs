using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TomatoTimerPro.Tools.Markdown;

/// <summary>
/// Converts the journal's Markdown into a small WPF document model. The editor can
/// therefore feel like a normal rich text editor while the storage format remains
/// Markdown.
/// </summary>
public static class MarkdownVisualDocument
{
    public static readonly DependencyProperty RawMarkdownProperty = DependencyProperty.RegisterAttached(
        "RawMarkdown", typeof(string), typeof(MarkdownVisualDocument), new PropertyMetadata(null));
    public static readonly DependencyProperty BlockKindProperty = DependencyProperty.RegisterAttached(
        "BlockKind", typeof(string), typeof(MarkdownVisualDocument), new PropertyMetadata(null));
    public static readonly DependencyProperty HeadingLevelProperty = DependencyProperty.RegisterAttached(
        "HeadingLevel", typeof(int), typeof(MarkdownVisualDocument), new PropertyMetadata(0));
    public static readonly DependencyProperty LinkTargetProperty = DependencyProperty.RegisterAttached(
        "LinkTarget", typeof(string), typeof(MarkdownVisualDocument), new PropertyMetadata(null));

    private static readonly Regex Heading = new(@"^\s*(#{1,3})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"^\s*<!--.*-->\s*$", RegexOptions.Compiled);

    public static void SetRawMarkdown(DependencyObject element, string? value) => element.SetValue(RawMarkdownProperty, value);
    public static string? GetRawMarkdown(DependencyObject element) => element.GetValue(RawMarkdownProperty) as string;
    public static void SetBlockKind(DependencyObject element, string? value) => element.SetValue(BlockKindProperty, value);
    public static string? GetBlockKind(DependencyObject element) => element.GetValue(BlockKindProperty) as string;
    public static void SetHeadingLevel(DependencyObject element, int value) => element.SetValue(HeadingLevelProperty, value);
    public static int GetHeadingLevel(DependencyObject element) => (int)element.GetValue(HeadingLevelProperty);
    public static void SetLinkTarget(DependencyObject element, string? value) => element.SetValue(LinkTargetProperty, value);
    public static string? GetLinkTarget(DependencyObject element) => element.GetValue(LinkTargetProperty) as string;

    public static FlowDocument ToFlowDocument(string? markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(18, 14, 18, 20),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(67, 59, 52))
        };
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (Comment.IsMatch(line))
            {
                var hidden = new Paragraph(new Run(line) { Foreground = Brushes.Transparent, FontSize = 1 })
                { Margin = new Thickness(0), LineHeight = 1 };
                SetRawMarkdown(hidden, line);
                document.Blocks.Add(hidden);
                index++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 3, 0, 3) });
                index++;
                continue;
            }
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var fence = line.Trim();
                var start = new Paragraph(new Run(fence) { FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(111, 91, 67)) })
                { Background = new SolidColorBrush(Color.FromRgb(246, 238, 220)), Padding = new Thickness(8, 4, 8, 2), Margin = new Thickness(0, 5, 0, 0) };
                SetBlockKind(start, "code");
                document.Blocks.Add(start);
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new Paragraph(new Run(lines[index]) { FontFamily = new FontFamily("Consolas") })
                    { Background = new SolidColorBrush(Color.FromRgb(246, 238, 220)), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0) };
                    SetBlockKind(code, "code");
                    document.Blocks.Add(code);
                    index++;
                }
                if (index < lines.Length)
                {
                    var end = new Paragraph(new Run(lines[index]) { FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(111, 91, 67)) })
                    { Background = new SolidColorBrush(Color.FromRgb(246, 238, 220)), Padding = new Thickness(8, 2, 8, 4), Margin = new Thickness(0, 0, 0, 5) };
                    SetBlockKind(end, "code");
                    document.Blocks.Add(end);
                    index++;
                }
                continue;
            }
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var paragraph = CreateParagraph(heading.Groups[2].Value);
                SetHeadingLevel(paragraph, level);
                paragraph.FontSize = level switch { 1 => 25, 2 => 21, _ => 18 };
                paragraph.FontWeight = FontWeights.Bold;
                paragraph.Margin = new Thickness(0, level == 1 ? 12 : 8, 0, 5);
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }
            if (Rule.IsMatch(line))
            {
                var paragraph = new Paragraph(new Run("────────────────────────") { Foreground = new SolidColorBrush(Color.FromRgb(190, 168, 131)) })
                { Margin = new Thickness(0, 8, 0, 8) };
                SetBlockKind(paragraph, "rule");
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }
            var quote = line.TrimStart().StartsWith(">", StringComparison.Ordinal);
            if (quote)
            {
                var paragraph = CreateParagraph(line.TrimStart()[1..].TrimStart());
                SetBlockKind(paragraph, "quote");
                paragraph.Margin = new Thickness(18, 4, 0, 4);
                paragraph.Padding = new Thickness(10, 4, 8, 4);
                paragraph.Background = new SolidColorBrush(Color.FromRgb(247, 239, 221));
                paragraph.Foreground = new SolidColorBrush(Color.FromRgb(126, 103, 76));
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }
            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                var numbered = Numbered.IsMatch(line);
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(22, 3, 0, 5),
                    Padding = new Thickness(0)
                };
                while (index < lines.Length)
                {
                    var match = numbered ? Numbered.Match(lines[index]) : Bullet.Match(lines[index]);
                    if (!match.Success) break;
                    var itemParagraph = CreateParagraph(match.Groups[1].Value);
                    list.ListItems.Add(new ListItem(itemParagraph));
                    index++;
                }
                document.Blocks.Add(list);
                continue;
            }
            document.Blocks.Add(CreateParagraph(line));
            index++;
        }
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    public static string ToMarkdown(FlowDocument document)
    {
        var lines = new List<string>();
        foreach (var block in document.Blocks) AppendBlock(block, lines);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static Paragraph CreateParagraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 6), LineHeight = 24 };
        AppendInline(paragraph.Inlines, text);
        return paragraph;
    }

    private static void AppendInline(InlineCollection target, string text)
    {
        var plainStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }
            if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*'
                && FindClosing(text, index + 2, "**", out var boldEnd))
            {
                AddPlain(target, text[plainStart..index]);
                var bold = new Bold();
                AppendInline(bold.Inlines, text[(index + 2)..boldEnd]);
                target.Add(bold);
                index = boldEnd + 1;
                plainStart = index + 1;
                index = plainStart - 1;
                continue;
            }
            if (text[index] == '*' && FindClosing(text, index + 1, "*", out var italicEnd))
            {
                AddPlain(target, text[plainStart..index]);
                var italic = new Italic();
                AppendInline(italic.Inlines, text[(index + 1)..italicEnd]);
                target.Add(italic);
                index = italicEnd;
                plainStart = index + 1;
                continue;
            }
            if (text[index] == '`' && FindClosing(text, index + 1, "`", out var codeEnd))
            {
                AddPlain(target, text[plainStart..index]);
                target.Add(new Run(text[(index + 1)..codeEnd])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(242, 233, 214)),
                    Foreground = new SolidColorBrush(Color.FromRgb(113, 82, 57))
                });
                index = codeEnd;
                plainStart = index + 1;
                continue;
            }
            if (index + 1 < text.Length && text[index] == '~' && text[index + 1] == '~'
                && FindClosing(text, index + 2, "~~", out var strikeEnd))
            {
                AddPlain(target, text[plainStart..index]);
                var strike = new Span();
                strike.TextDecorations = TextDecorations.Strikethrough;
                AppendInline(strike.Inlines, text[(index + 2)..strikeEnd]);
                target.Add(strike);
                index = strikeEnd + 1;
                plainStart = index + 1;
                index = plainStart - 1;
                continue;
            }
            if (index + 2 < text.Length && text[index] == '<' && text[index + 1] == 'u' && text[index + 2] == '>')
            {
                var underlineEnd = text.IndexOf("</u>", index + 3, StringComparison.Ordinal);
                if (underlineEnd >= 0)
                {
                    AddPlain(target, text[plainStart..index]);
                    var underline = new Underline();
                    AppendInline(underline.Inlines, text[(index + 3)..underlineEnd]);
                    target.Add(underline);
                    index = underlineEnd + 3;
                    plainStart = index + 1;
                    continue;
                }
            }
            if (text[index] == '[' && TryLink(text, index, out var labelEnd, out var urlEnd))
            {
                AddPlain(target, text[plainStart..index]);
                var label = text[(index + 1)..labelEnd];
                var url = text[(labelEnd + 2)..urlEnd];
                var link = new Hyperlink(new Run(label)) { NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null };
                SetLinkTarget(link, url);
                target.Add(link);
                index = urlEnd;
                plainStart = index + 1;
            }
        }
        AddPlain(target, text[plainStart..]);
    }

    private static void AddPlain(InlineCollection target, string text)
    {
        if (text.Length == 0) return;
        text = text.Replace("\\*", "*").Replace("\\`", "`").Replace("\\[", "[");
        target.Add(new Run(text));
    }

    private static bool FindClosing(string text, int start, string token, out int end)
    {
        end = text.IndexOf(token, start, StringComparison.Ordinal);
        return end >= start;
    }

    private static bool TryLink(string text, int start, out int labelEnd, out int urlEnd)
    {
        labelEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd < 0) { urlEnd = -1; return false; }
        urlEnd = text.IndexOf(')', labelEnd + 2);
        return urlEnd > labelEnd + 2;
    }

    private static void AppendBlock(Block block, List<string> lines)
    {
        var raw = GetRawMarkdown(block);
        if (raw is not null) { lines.Add(raw); return; }
        if (block is System.Windows.Documents.List list)
        {
            var number = 1;
            foreach (var item in list.ListItems)
            {
                var paragraph = item.Blocks.OfType<Paragraph>().FirstOrDefault();
                var content = paragraph is null ? string.Empty : InlineToMarkdown(paragraph.Inlines);
                lines.Add(list.MarkerStyle == TextMarkerStyle.Decimal ? $"{number++}. {content}" : $"- {content}");
            }
            return;
        }
        if (block is Paragraph paragraphBlock)
        {
            var kind = GetBlockKind(paragraphBlock);
            if (kind == "rule") { lines.Add("---"); return; }
            if (kind == "code") { lines.Add(InlineToMarkdown(paragraphBlock.Inlines)); return; }
            var content = InlineToMarkdown(paragraphBlock.Inlines);
            var heading = GetHeadingLevel(paragraphBlock);
            if (heading > 0) content = new string('#', heading) + " " + content;
            else if (kind == "quote") content = "> " + content;
            lines.Add(content);
            return;
        }
        if (block is Section section)
            foreach (var child in section.Blocks) AppendBlock(child, lines);
    }

    private static string InlineToMarkdown(InlineCollection inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines) builder.Append(InlineToMarkdown(inline));
        return builder.ToString();
    }

    private static string InlineToMarkdown(Inline inline)
    {
        var link = GetLinkTarget(inline);
        var content = inline switch
        {
            Run plainRun => FormatRun(plainRun),
            Hyperlink hyperlink => InlineToMarkdown(hyperlink.Inlines),
            Bold bold => $"**{InlineToMarkdown(bold.Inlines)}**",
            Italic italic => $"*{InlineToMarkdown(italic.Inlines)}*",
            Underline underline => $"<u>{InlineToMarkdown(underline.Inlines)}</u>",
            Span span => InlineToMarkdown(span.Inlines),
            LineBreak => "\n",
            _ => string.Empty
        };
        if (inline is Run codeRun && codeRun.FontFamily?.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true)
            content = $"`{content}`";
        var decorations = inline.GetValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        if (decorations?.Any(item => item.Location == TextDecorationLocation.Underline) == true && inline is not Underline)
            content = $"<u>{content}</u>";
        if (decorations?.Any(item => item.Location == TextDecorationLocation.Strikethrough) == true)
            content = $"~~{content}~~";
        return link is null ? content : $"[{content}]({link})";
    }

    private static string FormatRun(Run run)
    {
        var content = run.Text;
        if (run.FontWeight == FontWeights.Bold) content = $"**{content}**";
        if (run.FontStyle == FontStyles.Italic) content = $"*{content}*";
        return content;
    }
}
