using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed class TrackerEditor : ObservableObject
{
    private string title = "", unit = "", currency = "EUR", minimum = "1", maximum = "5", target = "", interval = "1";
    private TrackerValueType type = TrackerValueType.Decimal;
    private TrackerFrequency frequency = TrackerFrequency.Daily;
    private TrackerEntryMode mode;
    private TrackerAggregation aggregation = TrackerAggregation.Last;
    private DateTimeOffset? start, end;
    private bool locked;
    public string Title { get => title; set => SetProperty(ref title, value); }
    public string Unit { get => unit; set => SetProperty(ref unit, value); }
    public string Currency { get => currency; set => SetProperty(ref currency, value); }
    public string Minimum { get => minimum; set => SetProperty(ref minimum, value); }
    public string Maximum { get => maximum; set => SetProperty(ref maximum, value); }
    public string Target { get => target; set => SetProperty(ref target, value); }
    public string Interval { get => interval; set => SetProperty(ref interval, value); }
    public TrackerValueType Type { get => type; set { if (SetProperty(ref type, value)) { Unit = value == TrackerValueType.Distance ? "km" : ""; Target = ""; Aggregation = TrackerAggregation.Last; Notify(); } } }
    public TrackerFrequency Frequency { get => frequency; set { if (SetProperty(ref frequency, value)) Notify(); } }
    public TrackerEntryMode Mode { get => mode; set { if (SetProperty(ref mode, value)) Notify(); } }
    public TrackerAggregation Aggregation { get => aggregation; set => SetProperty(ref aggregation, value); }
    public DateTimeOffset? StartDate { get => start; set => SetProperty(ref start, value); }
    public DateTimeOffset? EndDate { get => end; set => SetProperty(ref end, value); }
    public bool Locked { get => locked; set { if (SetProperty(ref locked, value)) OnPropertyChanged(nameof(CanConfigure)); } }
    public bool CanConfigure => !Locked;
    public IReadOnlyList<TrackerValueType> Types { get; } = Enum.GetValues<TrackerValueType>();
    public IReadOnlyList<TrackerFrequency> Frequencies { get; } = Enum.GetValues<TrackerFrequency>();
    public IReadOnlyList<TrackerEntryMode> Modes { get; } = Enum.GetValues<TrackerEntryMode>();
    public IReadOnlyList<string> DistanceUnits { get; } = new[] { "m", "km", "mi" };
    public IReadOnlyList<TrackerAggregation> Aggregations => TrackerRules.Aggregations(Type).ToArray();
    public IReadOnlyList<TrackerWeekday> Days { get; } = Enum.GetValues<DayOfWeek>().Select(d => new TrackerWeekday(d)).ToArray();
    public bool HasUnit => Type is TrackerValueType.Integer or TrackerValueType.Decimal or TrackerValueType.CustomUnit;
    public bool IsCurrency => Type == TrackerValueType.Currency;
    public bool IsDistance => Type == TrackerValueType.Distance;
    public bool IsScale => Type == TrackerValueType.Scale;
    public bool IsInterval => Frequency == TrackerFrequency.EveryXDays;
    public bool IsWeekdays => Frequency == TrackerFrequency.SelectedWeekdays;
    public bool IsMultiple => Mode == TrackerEntryMode.Multiple;
    public string TargetHint => Type == TrackerValueType.Duration ? "Optional, e.g. 1:15:30" : Type == TrackerValueType.Boolean ? "Optional: true or false" : "Optional target";
    public TrackerDraft Build()
    {
        long? min = null, max = null;
        if (IsScale)
        {
            if (!long.TryParse(Minimum, NumberStyles.Integer, CultureInfo.CurrentCulture, out var a) || !long.TryParse(Maximum, NumberStyles.Integer, CultureInfo.CurrentCulture, out var b))
                throw new TrackerValidationException("Enter whole numbers for the scale bounds.");
            min = a; max = b;
        }
        var days = 1;
        if (IsInterval && !int.TryParse(Interval, out days)) throw new TrackerValidationException("Enter a whole number of days.");
        return new(Title, new(Type, Unit, Currency, min, max), new(Frequency, LocalDate(StartDate), LocalDate(EndDate), days,
            Days.Where(d => d.Selected).Sum(d => 1 << (int)d.Day)), string.IsNullOrWhiteSpace(Target) ? null : TrackerPresentation.Parse(Target, Type), Mode, Aggregation);
    }
    public void Load(TrackerDraft draft, bool hasHistory)
    {
        Title = draft.Title; Type = draft.Settings.Type; Unit = draft.Settings.Unit ?? ""; Currency = draft.Settings.CurrencyCode ?? "EUR";
        Minimum = draft.Settings.ScaleMin?.ToString(CultureInfo.CurrentCulture) ?? "1"; Maximum = draft.Settings.ScaleMax?.ToString(CultureInfo.CurrentCulture) ?? "5";
        Target = draft.Target is { } value ? TrackerPresentation.Input(value, Type) : "";
        Frequency = draft.Schedule.Frequency; StartDate = Picker(draft.Schedule.StartDate); EndDate = Picker(draft.Schedule.EndDate);
        Interval = draft.Schedule.Interval.ToString(CultureInfo.CurrentCulture); Mode = draft.EntryMode; Aggregation = draft.Aggregation;
        foreach (var day in Days) day.Selected = (draft.Schedule.Weekdays & (1 << (int)day.Day)) != 0;
        Locked = hasHistory;
    }
    public static DateOnly? LocalDate(DateTimeOffset? date) => date is { } d ? DateOnly.FromDateTime(d.DateTime) : null;
    public static DateTimeOffset? Picker(DateOnly? date) => date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
    private void Notify()
    {
        foreach (var name in new[] { nameof(HasUnit), nameof(IsCurrency), nameof(IsDistance), nameof(IsScale), nameof(IsInterval), nameof(IsWeekdays), nameof(IsMultiple), nameof(Aggregations), nameof(TargetHint) }) OnPropertyChanged(name);
    }
}
public sealed class TrackerWeekday(DayOfWeek day) : ObservableObject
{
    private bool selected;
    public DayOfWeek Day { get; } = day;
    public string Label => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(Day);
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
}
public static class TrackerPresentation
{
    public static TrackerValue Parse(string text, TrackerValueType type)
    {
        if (type == TrackerValueType.Boolean)
        {
            if (bool.TryParse(text.Trim(), out var value)) return new(Boolean: value);
            throw new TrackerValidationException("Choose true or false.");
        }
        try { return new(ExactValueText.ParseInput(text, type == TrackerValueType.Duration)); }
        catch (FormatException exception) { throw new TrackerValidationException(exception.Message); }
    }
    public static string Input(TrackerValue value, TrackerValueType type) => type == TrackerValueType.Boolean ? value.Boolean!.Value ? "true" : "false"
        : type == TrackerValueType.Duration ? ExactValueText.Duration(checked((long)value.Number!.Value)) : ExactValueText.Input(value.Number!.Value);
    public static string Amount(TrackerValue value, TrackerSettings settings) => Input(value, settings.Type) + (settings.Type switch
    {
        TrackerValueType.Percentage => "%", TrackerValueType.Currency => " " + settings.CurrencyCode,
        _ => settings.Unit is { } unit ? " " + unit : ""
    });
    public static string Frequency(TrackerSchedule schedule) => schedule.Frequency switch
    {
        TrackerFrequency.Unscheduled => "Unscheduled", TrackerFrequency.Daily => "Daily", TrackerFrequency.Weekly => "Weekly",
        TrackerFrequency.EveryXDays => $"Every {schedule.Interval} days",
        TrackerFrequency.Monthly => $"Monthly · day {schedule.StartDate?.Day}",
        _ => string.Join(", ", Enum.GetValues<DayOfWeek>().Where(d => (schedule.Weekdays & (1 << (int)d)) != 0).Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d)))
    };
}
public sealed record TrackerRow(Guid ProfileId, TrackerPeriod Period)
{
    public WorkspaceItemReference Reference => new(ProfileId, Period.Tracker.Item.Id);
    public string Title => Period.Tracker.Item.Title;
    public bool CanRecord => Period.Tracker.IsActive;
    public string RecordLabel => Period.Tracker.EntryMode == TrackerEntryMode.Multiple || Period.Value is null ? "Add value" : "Record value";
    public string Summary => (Period.Value is { } value ? TrackerPresentation.Amount(value, Period.Tracker.Settings) : "No entries")
        + (Period.Tracker.Target is { } target ? " / " + TrackerPresentation.Amount(target, Period.Tracker.Settings) : "")
        + (Period.PeriodDate is { } date ? $" · Period {date:d}" : "") + (Period.Pending ? " · Pending" : "");
    public string Frequency => TrackerPresentation.Frequency(Period.Tracker.Schedule);
}
public sealed record TrackerEntryRow(WorkspaceItemReference Reference, TrackerEntry Entry, TrackerSettings Settings)
{
    public string Label => $"{Entry.LocalDate:d}, {Entry.LocalTime:t} · {TrackerPresentation.Amount(Entry.Value, Settings)}";
    public string Note => Entry.Note ?? "";
}
