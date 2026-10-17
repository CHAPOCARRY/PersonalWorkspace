using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed record RichInline(string Text = "", bool Bold = false, bool Italic = false, string? Link = null, IReadOnlyList<RichInline>? Children = null);
public sealed record RichParagraph(IReadOnlyList<RichInline> Inlines, int Heading = 0, int Indent = 0, string Prefix = "");
public sealed record RichPreview(IReadOnlyList<RichParagraph> Paragraphs, bool Fallback);
public static class RichTextPresentation
{
    // A validated, renderer-neutral projection. Unsupported persisted content remains inert text.
    public static RichPreview Preview(RichTextDocument document)
    {
        try
        {
            _ = RichTextFormat.Extract(document);
            var parsed = Markdown.Parse(document.Content, new MarkdownPipelineBuilder { MaximumNestingDepth = 64 }.Build());
            var paragraphs = new List<RichParagraph>();
            IReadOnlyList<RichInline> Inlines(ContainerInline? parent) => parent is null ? [] : parent.Select<Inline, RichInline>(i => i switch
            {
                LiteralInline l => new(l.Content.ToString()),
                HtmlEntityInline e => new(e.Transcoded.ToString()),
                LineBreakInline => new("\n"),
                EmphasisInline e => new(Bold: e.DelimiterCount == 2, Italic: e.DelimiterCount == 1, Children: Inlines(e)),
                LinkInline l => new(Link: l.Url, Children: Inlines(l)),
                AutolinkInline l => new(l.Url, Link: l.IsEmail ? "mailto:" + l.Url : l.Url),
                _ => throw new ContentValidationException("Unsupported rich text.")
            }).ToArray();
            void Blocks(ContainerBlock parent, int depth = 0, string prefix = "")
            {
                foreach (var block in parent)
                {
                    switch (block)
                    {
                        case ParagraphBlock p: paragraphs.Add(new(Inlines(p.Inline), Indent: depth, Prefix: prefix)); prefix = ""; break;
                        case HeadingBlock h: paragraphs.Add(new(Inlines(h.Inline), h.Level, depth, prefix)); prefix = ""; break;
                        case ListBlock list:
                            var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                            foreach (var item in list.Cast<ListItemBlock>()) Blocks(item, depth + 1, list.IsOrdered ? number++ + ". " : "• ");
                            break;
                        default: throw new ContentValidationException("Unsupported rich text.");
                    }
                }
            }
            Blocks(parsed); return new(paragraphs, false);
        }
        catch (ContentValidationException) { return new([new([new(document.Content)])], true); }
    }
}
public enum RichFormatAction { Bold, Italic, Heading, BulletedList, NumberedList, Link }
public sealed record RichEditResult(string Text, int SelectionStart, int SelectionLength);
public static class RichTextEditing
{
    public static RichEditResult Apply(string text, int start, int length, RichFormatAction action)
    {
        start = Math.Clamp(start, 0, text.Length); length = Math.Clamp(length, 0, text.Length - start);
        var selected = text.Substring(start, length);
        if (action is RichFormatAction.Heading or RichFormatAction.BulletedList or RichFormatAction.NumberedList)
        {
            var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
            var end = text.IndexOf('\n', start + length); if (end < 0) end = text.Length;
            var lines = text[lineStart..end].Split('\n');
            var value = string.Join("\n", lines.Select((line, i) => (action == RichFormatAction.Heading ? "# " : action == RichFormatAction.BulletedList ? "- " : (i + 1) + ". ") + line));
            return new(text[..lineStart] + value + text[end..], lineStart, value.Length);
        }
        var marker = action == RichFormatAction.Bold ? "**" : "*";
        var body = selected.Length == 0 ? action == RichFormatAction.Link ? "link text" : "text" : selected;
        var replacement = action == RichFormatAction.Link ? "[" + body + "](https://example.com)" : marker + body + marker;
        return new(text[..start] + replacement + text[(start + length)..], start + (action == RichFormatAction.Link ? 1 : marker.Length), body.Length);
    }
}
