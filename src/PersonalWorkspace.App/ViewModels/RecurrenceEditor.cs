using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class RecurrenceEditor : ObservableObject
{
    public IReadOnlyList<string> Patterns { get; } = new[] { "Daily", "Weekly / selected weekdays", "Monthly by day", "Monthly ordinal weekday" };
    public IReadOnlyList<string> Ordinals { get; } = new[] { "First", "Second", "Third", "Fourth", "Fifth", "Last" };
    public IReadOnlyList<DayOfWeek> WeekdayOptions { get; } = Enum.GetValues<DayOfWeek>();
    private int patternIndex;
    public int PatternIndex { get=>patternIndex; set { if(SetProperty(ref patternIndex,value)) OnPatternIndexChanged(value); } }
    private string interval = "1";
    public string Interval { get=>interval; set { SetProperty(ref interval,value); } }
    private string start = "";
    public string Start { get=>start; set { SetProperty(ref start,value); } }
    private string end = "";
    public string End { get=>end; set { SetProperty(ref end,value); } }
    private string monthDay = "1";
    public string MonthDay { get=>monthDay; set { SetProperty(ref monthDay,value); } }
    private int ordinalIndex;
    public int OrdinalIndex { get=>ordinalIndex; set { SetProperty(ref ordinalIndex,value); } }
    private DayOfWeek weekday = DayOfWeek.Monday;
    public DayOfWeek Weekday { get=>weekday; set { SetProperty(ref weekday,value); } }
    private bool monday,tuesday,wednesday,thursday,friday,saturday,sunday;
    public bool Monday { get=>monday; set=>SetProperty(ref monday,value); }
    public bool Tuesday { get=>tuesday; set=>SetProperty(ref tuesday,value); }
    public bool Wednesday { get=>wednesday; set=>SetProperty(ref wednesday,value); }
    public bool Thursday { get=>thursday; set=>SetProperty(ref thursday,value); }
    public bool Friday { get=>friday; set=>SetProperty(ref friday,value); }
    public bool Saturday { get=>saturday; set=>SetProperty(ref saturday,value); }
    public bool Sunday { get=>sunday; set=>SetProperty(ref sunday,value); }
    public bool IsWeekly => PatternIndex == 1;
    public bool IsMonthlyDay => PatternIndex == 2;
    public bool IsMonthlyWeekday => PatternIndex == 3;
    private void OnPatternIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeekly)); OnPropertyChanged(nameof(IsMonthlyDay)); OnPropertyChanged(nameof(IsMonthlyWeekday));
    }
    public RecurrenceRule Build()
    {
        var first = ParseDate(Start);
        if (!int.TryParse(Interval,out var every)) throw new TaskValidationException("Enter a whole recurrence interval.");
        if (!int.TryParse(MonthDay,out var day)) day = 0;
        var days = (Sunday?1:0)|(Monday?2:0)|(Tuesday?4:0)|(Wednesday?8:0)|(Thursday?16:0)|(Friday?32:0)|(Saturday?64:0);
        return new RecurrenceRule((RecurrencePattern)PatternIndex,first,every,days,day,OrdinalIndex==5?-1:OrdinalIndex+1,Weekday,
            string.IsNullOrWhiteSpace(End)?null:ParseDate(End)).Validate();
    }
    public static DateOnly ParseDate(string text) => DateOnly.TryParse(text,CultureInfo.CurrentCulture,DateTimeStyles.None,out var date)
        ? date : throw new TaskValidationException("Enter a date using your Windows date format.");
    public void Load(RecurrenceRule? rule)
    {
        PatternIndex=(int)(rule?.Pattern??RecurrencePattern.Daily); Interval=(rule?.Interval??1).ToString(CultureInfo.CurrentCulture);
        Start=rule?.StartDate.ToString("d",CultureInfo.CurrentCulture)??""; End=rule?.EndDate?.ToString("d",CultureInfo.CurrentCulture)??"";
        MonthDay=(rule?.MonthDay??1).ToString(CultureInfo.CurrentCulture); OrdinalIndex=rule?.Ordinal==-1?5:(rule?.Ordinal??1)-1;
        Weekday=rule?.Weekday??DayOfWeek.Monday; var days=rule?.Weekdays??0;
        Sunday=(days&1)!=0; Monday=(days&2)!=0; Tuesday=(days&4)!=0; Wednesday=(days&8)!=0; Thursday=(days&16)!=0; Friday=(days&32)!=0; Saturday=(days&64)!=0;
    }
    public static string Summary(RecurrenceRule rule) => rule.Pattern switch
    {
        RecurrencePattern.Daily => rule.Interval==1?"Daily":$"Every {rule.Interval} days",
        RecurrencePattern.Weekly => $"Every {rule.Interval} week(s) on "+string.Join(", ",Enum.GetValues<DayOfWeek>().Where(day=>(rule.Weekdays&(1<<(int)day))!=0)),
        RecurrencePattern.MonthlyDay => $"Day {rule.MonthDay} every {rule.Interval} month(s)",
        _ => $"{(rule.Ordinal==-1?"Last":new[]{"First","Second","Third","Fourth","Fifth"}[rule.Ordinal-1])} {rule.Weekday} every {rule.Interval} month(s)"
    };
}

public sealed record OccurrenceRow(Guid ProfileId, OccurrenceItem Item)
{
    public string Label => $"{Item.Occurrence.OccurrenceDate:d} · {Item.Occurrence.Status}" + (Item.Occurrence.IsSkipped?" · Skipped":"")
        + (Item.Occurrence.OccurrenceDate!=Item.Occurrence.SlotDate?$" · Moved from {Item.Occurrence.SlotDate:d}":"");
    public string ValueText => Item.Occurrence.IsSkipped ? "Skipped · No work required" : TaskValuePresentation.Progress(Item.Value);
}
