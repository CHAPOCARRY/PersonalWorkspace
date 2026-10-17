using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PersonalWorkspace.Core;
using PersonalWorkspace.Services;

namespace PersonalWorkspace.App.Views;

// Reusable native view/editor. Only Markdown is passed back to the service boundary.
public sealed class RichContentView : UserControl
{
    private readonly RichTextDocument document;
    private readonly Func<string, Task<bool>> save;
    private readonly Func<Uri, Task> openLink;
    private bool editing;
    private bool editable;
    private Button? editButton;
    public bool Editable { get => editable; set { editable = value; if (editButton is not null) editButton.IsEnabled = value; } }
    public RichContentView(RichTextDocument document, Func<string, Task<bool>> save, Func<Uri, Task> openLink)
    {
        this.document = document; this.save = save; this.openLink = openLink;
        ShowPreview();
    }
    private void ShowPreview()
    {
        editing = false;
        var panel = new StackPanel { Spacing = 8 };
        var preview = RichTextPresentation.Preview(document);
        if (document.Content.Length == 0) panel.Children.Add(new TextBlock { Text = "Add text…", Opacity = 0.65 });
        else
        {
            var view = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(view, "RichTextPreview");
            void Append(InlineCollection target, IReadOnlyList<RichInline> source)
            {
                foreach (var part in source)
                {
                    Span? span = part.Link is { } uri ? new Hyperlink() : part.Bold ? new Bold() : part.Italic ? new Italic() : part.Children is not null ? new Span() : null;
                    if (span is Hyperlink link)
                    {
                        var destination = new Uri(part.Link!);
                        ToolTipService.SetToolTip(link, destination.Scheme == "personalworkspace" ? "Internal link: " + destination : destination.ToString());
                        link.Click += async (_, _) => { try { await openLink(destination); } catch (Exception) { } };
                    }
                    if (span is null) target.Add(new Run { Text = part.Text });
                    else { if (part.Text.Length > 0) span.Inlines.Add(new Run { Text = part.Text }); if (part.Children is not null) Append(span.Inlines, part.Children); target.Add(span); }
                }
            }
            foreach (var paragraph in preview.Paragraphs)
            {
                var block = new Paragraph { FontSize = paragraph.Heading == 0 ? 14 : Math.Max(16, 28 - paragraph.Heading * 2), TextIndent = Math.Max(0, paragraph.Indent - 1) * 16 };
                if (paragraph.Heading > 0) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (paragraph.Prefix.Length > 0) block.Inlines.Add(new Run { Text = paragraph.Prefix });
                Append(block.Inlines, paragraph.Inlines); view.Blocks.Add(block);
            }
            panel.Children.Add(view);
            if (preview.Fallback) panel.Children.Add(new TextBlock { Text = "Unsupported formatting shown as plain text.", TextWrapping = TextWrapping.Wrap });
        }
        editButton = new Button { Content = "Edit", IsEnabled = Editable };
        AutomationProperties.SetAutomationId(editButton, "RichTextEdit");
        editButton.Click += (_, _) => { if (Editable) ShowEditor(); };
        panel.Children.Add(editButton); Content = panel;
    }
    private void ShowEditor()
    {
        editing = true;
        var panel = new StackPanel { Spacing = 8 };
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = document.Content, MaxLength = 1_000_000, MinHeight = 100, MaxHeight = 300 };
        AutomationProperties.SetAutomationId(input, "RichTextMarkdown"); AutomationProperties.SetName(input, "Markdown text");
        var toolbar = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++) toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.RowDefinitions.Add(new() { Height = GridLength.Auto }); toolbar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var index = 0;
        foreach (var action in Enum.GetValues<RichFormatAction>())
        {
            var button = new Button { Content = action switch { RichFormatAction.BulletedList => "Bullets", RichFormatAction.NumberedList => "Numbers", _ => action.ToString() }, Padding = new(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, "Format " + action);
            button.Click += (_, _) =>
            {
                var result = RichTextEditing.Apply(input.Text, input.SelectionStart, input.SelectionLength, action);
                input.Text = result.Text; input.Select(result.SelectionStart, result.SelectionLength); input.Focus(FocusState.Programmatic);
            };
            Grid.SetRow(button, index / 3); Grid.SetColumn(button, index % 3); index++; toolbar.Children.Add(button);
        }
        panel.Children.Add(toolbar); panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var commit = new Button { Content = "Save" }; var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetAutomationId(commit, "RichTextSave"); AutomationProperties.SetAutomationId(cancel, "RichTextCancel");
        commit.Click += async (_, _) =>
        {
            if (!Editable || !editing) return;
            commit.IsEnabled = cancel.IsEnabled = false;
            try { await save(input.Text); }
            finally { commit.IsEnabled = cancel.IsEnabled = true; }
        };
        cancel.Click += (_, _) => ShowPreview();
        buttons.Children.Add(commit); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
        input.Focus(FocusState.Programmatic);
    }
}
