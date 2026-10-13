using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public static class RichTextFormat
{
    public const string Name = "markdown-subset";
    public const int Version = 1;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 64 }.Build();
    public static string Validate(string content)
    {
        if (content is null || content.Length > 1_000_000 || content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ContentValidationException("Use up to 1,000,000 characters of supported Markdown text.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(content); }
        catch (EncoderFallbackException) { throw new ContentValidationException("The document contains invalid Unicode."); }
        var canonical = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        _ = Plain(canonical); return canonical;
    }
    public static string Extract(RichTextDocument document)
    {
        if (document.Format != Name || document.FormatVersion != Version) throw new ContentValidationException("This rich-text format version is not supported.");
        return Plain(Validate(document.Content));
    }
    private static string Plain(string content)
    {
        MarkdownDocument document;
        try { document = Markdown.Parse(content, Pipeline); }
        catch (InvalidOperationException) { throw new ContentValidationException("The Markdown nesting is too deep."); }
        var text = new StringBuilder();
        void Inlines(ContainerInline? container)
        {
            if (container is null) return;
            foreach (var inline in container)
                switch (inline)
                {
                    case LiteralInline literal: text.Append(literal.Content); break;
                    case HtmlEntityInline entity: text.Append(entity.Transcoded); break;
                    case LineBreakInline: text.Append('\n'); break;
                    case EmphasisInline emphasis when emphasis.DelimiterCount is 1 or 2: Inlines(emphasis); break;
                    case LinkInline link when !link.IsImage: Link(link.Url); Inlines(link); break;
                    case AutolinkInline link: Link(link.IsEmail ? "mailto:" + link.Url : link.Url); text.Append(link.Url); break;
                    default: throw new ContentValidationException("Use paragraphs, headings, lists, bold, italic and links. HTML, images, code and embeds are not supported in this format version.");
                }
        }
        void Blocks(ContainerBlock parent)
        {
            foreach (var block in parent)
                switch (block)
                {
                    case ParagraphBlock paragraph: Inlines(paragraph.Inline); text.Append("\n\n"); break;
                    case HeadingBlock heading: Inlines(heading.Inline); text.Append("\n\n"); break;
                    case ListBlock list:
                        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                        foreach (var child in list.Cast<ListItemBlock>())
                        { text.Append(list.IsOrdered ? number++ + ". " : "- "); Blocks(child); while (text.Length > 0 && text[^1] == '\n') text.Length--; text.Append('\n'); }
                        text.Append('\n'); break;
                    default: throw new ContentValidationException("This Markdown block is not supported. Use paragraphs, headings and lists.");
                }
        }
        Blocks(document); return text.ToString().TrimEnd('\n');
    }
    private static void Link(string? target)
    {
        if (target is null || target.Any(char.IsControl) || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) throw new ContentValidationException("Links must use an absolute, supported URI.");
        if (uri.Scheme is "http" or "https" && uri.Host.Length > 0 || uri.Scheme == "mailto" && uri.AbsolutePath.Length > 0) return;
        if (uri.Scheme == "personalworkspace" && uri.Host == "item" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 && uri.IsDefaultPort
            && Guid.TryParseExact(uri.AbsolutePath.TrimStart('/'), "D", out var id) && id != Guid.Empty && uri.AbsolutePath.Count(c => c == '/') == 1) return;
        throw new ContentValidationException("Use http, https, mailto or personalworkspace://item/{guid} links.");
    }
}
