using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public static class PageTemplateLinks
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 64 }.UsePreciseSourceLocation().Build();
    public static string Remap(string content, Guid source, Guid destination)
    {
        content = RichTextFormat.Validate(content);
        var document = Markdown.Parse(content, Pipeline); var spans = new HashSet<(int Start, int Length)>();
        bool Self(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "personalworkspace" && uri.Host == "item" && Guid.TryParse(uri.AbsolutePath.TrimStart('/'), out var id) && id == source;
        void Add(SourceSpan span)
        {
            if (span.Start < 0 || span.End >= content.Length || span.End < span.Start) throw new TemplateValidationException("A self-link could not be safely remapped.");
            var raw = content.Substring(span.Start, span.Length);
            if (raw.StartsWith('<') && raw.EndsWith('>')) { span.Start++; span.End--; }
            spans.Add((span.Start, span.Length));
        }
        foreach (var link in document.Descendants<LinkInline>())
            if (Self(link.Url)) Add(link.Reference?.UrlSpan ?? link.UrlSpan);
        foreach (var link in document.Descendants<AutolinkInline>()) if (Self(link.Url)) Add(link.Span);
        var result = new StringBuilder(content); var url = $"personalworkspace://item/{destination:D}";
        foreach (var span in spans.OrderByDescending(s => s.Start)) { result.Remove(span.Start, span.Length); result.Insert(span.Start, url); }
        return RichTextFormat.Validate(result.ToString());
    }
}
