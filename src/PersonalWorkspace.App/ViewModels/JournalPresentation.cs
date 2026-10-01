using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record JournalSummaryRow(Guid ProfileId, JournalDefinition Journal, DateOnly Date, string Summary, bool HasContent)
{
    public string Title => Journal.Item.Title;
    public WorkspaceItemReference Reference => new(ProfileId, Journal.Item.Id);
    public string State => HasContent ? "Entry started" : "Empty";
}
public sealed record JournalChoice(Guid? Id, string Label);
public sealed class JournalOptionInput(Guid id, string label, bool selected = false) : ObservableObject
{
    private bool selected = selected;
    public Guid Id { get; } = id;
    public string Label { get; } = label;
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
}
public sealed class JournalFieldInput : ObservableObject
{
    private string text = "";
    private JournalChoice? choice;
    private DateTimeOffset? date;
    public JournalField Field { get; }
    public string Text { get => text; set => SetProperty(ref text, value); }
    public JournalChoice? Choice { get => choice; set => SetProperty(ref choice, value); }
    public DateTimeOffset? Date { get => date; set => SetProperty(ref date, value); }
    public IReadOnlyList<JournalChoice> Choices { get; }
    public IReadOnlyList<JournalOptionInput> Options { get; }
    public JournalFieldInput(JournalField field, JournalValue? value, IReadOnlyList<WorkspaceItem> references)
    {
        Field = field;
        Text = value?.Text ?? (value?.Number is { } number ? ExactValueText.Input(number) : value?.Integer is { } integer ? field.Type == JournalFieldType.Duration ? ExactValueText.Duration(integer) : integer.ToString(CultureInfo.CurrentCulture) : "");
        Date = value?.Date is { } day ? JournalPresentation.Picker(day) : null;
        Options = field.OrderedOptions.Select(o => new JournalOptionInput(o.Id, o.Label, value?.OptionIds?.Contains(o.Id) == true)).ToArray();
        Choices = new[] { new JournalChoice(null, "Not set") }.Concat(field.Type == JournalFieldType.Checkbox
            ? new[] { new JournalChoice(Guid.Empty, "false"), new JournalChoice(new Guid("00000000-0000-0000-0000-000000000001"), "true") }
            : field.Type == JournalFieldType.Select ? field.OrderedOptions.Select(o => new JournalChoice(o.Id, o.Label))
            : references.Where(r => r.ItemType == (field.Type == JournalFieldType.TaskReference ? WorkspaceItemType.Task : WorkspaceItemType.Tracker)).Select(r => new JournalChoice(r.Id, JournalPresentation.Reference(r)))).ToArray();
        var selected = field.Type == JournalFieldType.Checkbox ? value?.Integer is { } boolean ? Choices[(int)boolean + 1].Id : null
            : field.Type == JournalFieldType.Select ? value?.OptionIds?.FirstOrDefault() : value?.ReferenceId;
        Choice = Choices.FirstOrDefault(c => c.Id == selected) ?? Choices[0];
    }
    public JournalValue? Build()
    {
        try
        {
            return Field.Type switch
            {
                JournalFieldType.Checkbox => Choice?.Id is null ? null : new(Integer: Choice.Label == "true" ? 1 : 0),
                JournalFieldType.Select => Choice?.Id is { } id ? new(OptionIds: [id]) : null,
                JournalFieldType.MultiSelect => new(OptionIds: Options.Where(o => o.Selected).Select(o => o.Id).ToArray()),
                JournalFieldType.TaskReference or JournalFieldType.TrackerReference => Choice?.Id is { } id ? new(ReferenceId: id) : null,
                JournalFieldType.Date => Date is { } date ? new(Date: DateOnly.FromDateTime(date.DateTime)) : null,
                _ when string.IsNullOrWhiteSpace(Text) => null,
                JournalFieldType.Number or JournalFieldType.Percentage or JournalFieldType.Currency => new(Number: ExactValueText.ParseInput(Text)),
                JournalFieldType.Duration => new(Integer: checked((long)ExactValueText.ParseInput(Text, true))),
                JournalFieldType.Scale => new(Integer: long.Parse(Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture)),
                _ => new(Text: Text)
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException) { throw new JournalValidationException($"{Field.Name}: {exception.Message}"); }
    }
}
public sealed class JournalFieldEditor : ObservableObject
{
    private string name = "", currency = "EUR", minimum = "1", maximum = "5";
    private JournalFieldType type;
    public Guid? Id { get; private set; }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Currency { get => currency; set => SetProperty(ref currency, value); }
    public string Minimum { get => minimum; set => SetProperty(ref minimum, value); }
    public string Maximum { get => maximum; set => SetProperty(ref maximum, value); }
    public JournalFieldType Type { get => type; set { if (SetProperty(ref type, value)) { OnPropertyChanged(nameof(IsCurrency)); OnPropertyChanged(nameof(IsScale)); OnPropertyChanged(nameof(IsOptions)); } } }
    public bool IsCurrency => Type == JournalFieldType.Currency;
    public bool IsScale => Type == JournalFieldType.Scale;
    public bool IsOptions => Type is JournalFieldType.Select or JournalFieldType.MultiSelect;
    public IReadOnlyList<JournalFieldType> Types { get; } = Enum.GetValues<JournalFieldType>();
    public System.Collections.ObjectModel.ObservableCollection<JournalOptionEditor> Options { get; } = [];
    public bool HistoryLocked { get; private set; }
    public void Load(JournalField field)
    {
        Id = field.Id; Name = field.Name; Type = field.Type; Currency = field.CurrencyCode ?? "EUR"; Minimum = field.ScaleMin?.ToString() ?? "1"; Maximum = field.ScaleMax?.ToString() ?? "5"; HistoryLocked = field.HistoryLocked;
        Options.Clear(); foreach (var option in field.OrderedOptions) Options.Add(new(option.Id, option.Label));
    }
    public JournalFieldDraft Build()
    {
        if (IsScale && (!long.TryParse(Minimum, out _) || !long.TryParse(Maximum, out _))) throw new JournalValidationException("Enter whole-number scale bounds.");
        return new(Name, Type, Currency, IsScale ? long.Parse(Minimum) : null, IsScale ? long.Parse(Maximum) : null, Options.Select((o, i) => new JournalOption(o.Id, o.Label, i)).ToArray());
    }
}
public sealed class JournalOptionEditor(Guid id, string label) : ObservableObject
{
    private string label = label;
    public Guid Id { get; } = id;
    public string Label { get => label; set => SetProperty(ref label, value); }
}
public static class JournalPresentation
{
    public static DateTimeOffset Picker(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue));
    public static string Reference(WorkspaceItem item) => item.Title + (item.DeletedAtUtc is not null ? " (in Trash)" : item.ArchivedAtUtc is not null ? " (archived)" : "");
    public static string Value(JournalField field, JournalValue value, IReadOnlyList<WorkspaceItem> references) => field.Type switch
    {
        JournalFieldType.Currency => ExactValueText.Input(value.Number!.Value) + " " + field.CurrencyCode,
        JournalFieldType.Percentage => ExactValueText.Input(value.Number!.Value) + "%",
        JournalFieldType.Number => ExactValueText.Input(value.Number!.Value),
        JournalFieldType.Duration => ExactValueText.Duration(value.Integer!.Value),
        JournalFieldType.Scale => $"{value.Integer} ({field.ScaleMin}–{field.ScaleMax})",
        JournalFieldType.Checkbox => value.Integer == 1 ? "true" : "false",
        JournalFieldType.Date => value.Date?.ToString("d") ?? "",
        JournalFieldType.Select or JournalFieldType.MultiSelect => string.Join(", ", field.OrderedOptions.Where(o => value.OptionIds?.Contains(o.Id) == true).Select(o => o.Label)),
        JournalFieldType.TaskReference or JournalFieldType.TrackerReference => references.FirstOrDefault(r => r.Id == value.ReferenceId) is { } item ? Reference(item) : "Reference removed",
        _ => value.Text ?? ""
    };
    public static IReadOnlyList<JournalSummaryRow> Rows(Guid profile, DateOnly date, JournalSnapshot snapshot) => snapshot.Journals.Select(journal =>
    {
        var entry = snapshot.Entries.FirstOrDefault(e => e.JournalId == journal.Item.Id && e.Date == date);
        var parts = journal.Fields.Where(f => entry?.Values.ContainsKey(f.Id) == true).Take(2).Select(f =>
        {
            var text = Value(f, entry!.Values[f.Id], snapshot.References).ReplaceLineEndings(" ");
            return f.Name + ": " + (text.Length > 80 ? text[..80] + "…" : text);
        }).ToArray();
        return new JournalSummaryRow(profile, journal, date, parts.Length == 0 ? "No entry" : string.Join(" · ", parts), entry?.Values.Count > 0);
    }).ToArray();
}
