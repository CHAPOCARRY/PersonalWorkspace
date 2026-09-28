using CommunityToolkit.Mvvm.Input;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class TaskWorkspaceViewModel
{
    private IRecurrenceService? recurrence;
    public RecurrenceEditor RecurrenceEditor { get; } = new();
    public TaskValueEditor OccurrenceValue { get; } = new();
    private OccurrenceItem? occurrenceDetail;
    private IReadOnlyList<RecurrenceSegment> segments = [];
    private string occurrenceDate = "", historyFrom = "", historyThrough = "";
    private TaskStatus occurrenceStatus;
    private bool occurrenceSkipped, futureScope;
    private bool carryDeficit, carrySurplus, carryLocked;
    public bool CarryDeficit { get=>carryDeficit; set=>SetProperty(ref carryDeficit,value); }
    public bool CarrySurplus { get=>carrySurplus; set=>SetProperty(ref carrySurplus,value); }
    public bool ShowCarrySettings => IsSeries && Detail is { } row && CarrySettings.Supports(row.Task.ValueType);
    public bool CanEditCarrySettings => IsIdle && !carryLocked;
    public string CarryPolicyNotice => carryLocked ? "Carry settings are locked to preserve this series' execution history. Create a new series for another policy."
        : "Unrecorded results never create carry. Surplus covers only the next occurrence; extra credit is discarded. Settings lock once execution history exists.";
    private string Amount(decimal value) => occurrenceDetail?.Definition.Value is { } definition ? TaskValuePresentation.Amount(definition,value) : "";
    public string OccurrenceBaseTarget => Amount(occurrenceDetail?.Occurrence.Calculation?.BaseTarget ?? occurrenceDetail?.Definition.Value?.Target ?? 0);
    public string OccurrenceIncoming => occurrenceDetail?.Occurrence.Calculation is { } calculation
        ? calculation.CarryIn > 0 ? "Unfinished amount carried in: " + Amount(calculation.CarryIn)
            : calculation.CarryIn < 0 ? "Surplus credit: " + Amount(-calculation.CarryIn) : "No carry from prior occurrence" : "";
    public string OccurrenceEffectiveTarget => Amount(occurrenceDetail?.Occurrence.Calculation?.EffectiveTarget ?? occurrenceDetail?.Definition.Value?.Target ?? 0);
    public string OccurrenceOutgoing => occurrenceDetail?.Occurrence.Calculation is { } calculation
        ? calculation.CarryOut > 0 ? "Unfinished amount carried forward: " + Amount(calculation.CarryOut)
            : calculation.CarryOut < 0 ? "Surplus for next occurrence: " + Amount(-calculation.CarryOut) : "No carry forward" : "";
    public string OccurrenceCarryNotice => occurrenceDetail?.Occurrence.IsSkipped == true ? "Skipped · No work required. Prior carry passes to the next unskipped occurrence."
        : occurrenceDetail?.Occurrence.Calculation?.EffectiveTarget == 0 ? "Covered by previous surplus. No result is required; subtasks and dependencies still apply."
        : "Carry follows original slot order, even after a calendar move. Saving a result updates affected later occurrences.";
    public bool IsOccurrence => navigation.Current.Destination == "Occurrence";
    public bool IsDefinitionEditor => IsEditor && !IsOccurrence;
    public bool IsOneOffDetail => IsDetail && Detail?.Task.IsRecurring != true;
    public bool CanEditDefinitionExecution => IsIdle && Detail?.Task.IsRecurring != true;
    public bool CanEditValueDefinition => IsIdle && Detail?.Task.HasOccurrences != true;
    public bool IsSeries => Detail?.Task.IsRecurring == true;
    public bool ShowRecurrence => IsDetail && recurrence is not null;
    public string SeriesNotice => IsSeries ? "Recurring definition · Open an occurrence to change its date, status or Actual."
        : "Enable recurrence after saving this task. Reopen it and clear any one-off Actual first; its scheduled date will be replaced by the recurrence schedule.";
    public string SeriesSummary => segments.LastOrDefault(s=>s.Enabled) is { } segment ? global::PersonalWorkspace.App.ViewModels.RecurrenceEditor.Summary(segment.Rule) : "Does not repeat";
    public string OccurrenceTitle => occurrenceDetail?.Definition.Item.Title??"";
    public string OccurrenceContext => occurrenceDetail is { } item ? $"Occurrence · {item.Occurrence.OccurrenceDate:D} · Original slot {item.Occurrence.SlotDate:d}" : "";
    public string OccurrenceDate { get=>occurrenceDate; set=>SetProperty(ref occurrenceDate,value); }
    public TaskStatus OccurrenceStatus { get=>occurrenceStatus; set=>SetProperty(ref occurrenceStatus,value); }
    public bool OccurrenceSkipped { get=>occurrenceSkipped; set=>SetProperty(ref occurrenceSkipped,value); }
    public bool FutureScope { get=>futureScope; set { if(SetProperty(ref futureScope,value) && value && occurrenceDetail is { } item) RecurrenceEditor.Start=item.Occurrence.SlotDate.ToString("d"); } }
    public string HistoryFrom { get=>historyFrom; set=>SetProperty(ref historyFrom,value); }
    public string HistoryThrough { get=>historyThrough; set=>SetProperty(ref historyThrough,value); }
    public IReadOnlyList<OccurrenceRow> OccurrenceRows { get; private set; } = [];
    public bool HasOccurrenceRows => OccurrenceRows.Count > 0;

    private void ClearRecurrence()
    {
        occurrenceDetail=null; segments=[]; OccurrenceRows=[]; RecurrenceEditor.Load(null); OccurrenceValue.Load(null);
        OccurrenceDate=HistoryFrom=HistoryThrough=""; OccurrenceSkipped=FutureScope=false;
        CarryDeficit=CarrySurplus=carryLocked=false;
    }
    private async Task LoadRecurrenceAsync(TaskReference task,int request)
    {
        if(recurrence is null) return;
        var loaded=await recurrence.GetSegmentsAsync(task); if(request!=revision) return;
        segments=loaded; RecurrenceEditor.Load(loaded.LastOrDefault(s=>s.Enabled)?.Rule);
        if (HistoryFrom.Length==0 || HistoryThrough.Length==0)
        {
            var today=DateOnly.FromDateTime(clock.GetLocalNow().DateTime); HistoryFrom=today.AddDays(-30).ToString("d"); HistoryThrough=today.AddDays(30).ToString("d");
        }
        var history=await recurrence.GetHistoryAsync(task,global::PersonalWorkspace.App.ViewModels.RecurrenceEditor.ParseDate(HistoryFrom),global::PersonalWorkspace.App.ViewModels.RecurrenceEditor.ParseDate(HistoryThrough));
        if(request!=revision) return;
        if(history.Count>0 && Detail is { } row) Detail=row with { Task=row.Task with { HasOccurrences=true } };
        OccurrenceRows=history.Select(item=>new OccurrenceRow(task.ProfileId,item)).ToArray();
        var policy=await recurrence.GetCarrySettingsAsync(task); if(request!=revision)return;
        CarryDeficit=policy.Deficit; CarrySurplus=policy.Surplus; carryLocked=policy.Locked;
    }
    private async Task LoadOccurrenceAsync(Guid profile,string? id,int request)
    {
        if(recurrence is null || !Guid.TryParse(id,out var occurrenceId)) throw new TaskValidationException("This occurrence link is invalid.");
        var item=await recurrence.FindAsync(new(profile,occurrenceId)); if(request!=revision) return;
        occurrenceDetail=item; editorProfile=profile; editorReference=new(profile,item.Definition.Item.Id); Detail=null;
        OccurrenceDate=item.Occurrence.OccurrenceDate.ToString("d"); OccurrenceStatus=item.Occurrence.Status; OccurrenceSkipped=item.Occurrence.IsSkipped;
        OccurrenceValue.Load(item.Value,occurrence:true); FutureScope=false;
        var loaded=await recurrence.GetSegmentsAsync(editorReference); if(request!=revision)return;
        segments=loaded; RecurrenceEditor.Load(loaded.FirstOrDefault(s=>s.Id==item.Occurrence.SegmentId)?.Rule??loaded.LastOrDefault(s=>s.Enabled)?.Rule);
    }
    [RelayCommand] private void OpenOccurrence(OccurrenceRow row)
    {
        if(!IsIdle || row.ProfileId!=current.Current?.Id)return;
        navigation.Navigate(new("Occurrence",row.Item.Occurrence.Id.ToString("D")));
    }
    [RelayCommand] private void OpenSeries()
    {
        if(IsIdle && occurrenceDetail is { } item) navigation.Navigate(new("Task",item.Definition.Item.Id.ToString("D")));
    }
    [RelayCommand] private Task SaveRecurrenceAsync()=>MutateAsync(async()=>
    {
        if(recurrence is null || editorReference is not { } task)return;
        await recurrence.SetRuleAsync(task,RecurrenceEditor.Build(),IsOccurrence && FutureScope?RecurrenceScope.ThisAndFuture:RecurrenceScope.EntireSeries,
            IsOccurrence?occurrenceDetail?.Occurrence.Id:null);
    });
    [RelayCommand] private Task RemoveRecurrenceAsync()=>MutateAsync(async()=>
    {
        if(recurrence is not null && editorReference is { } task)await recurrence.RemoveAsync(task);
    });
    [RelayCommand] private Task SaveCarrySettingsAsync()=>MutateAsync(async()=>
    {
        if(recurrence is not null && editorReference is { } task)
            await recurrence.SetCarrySettingsAsync(task,CarryDeficit,CarrySurplus);
    });
    [RelayCommand] private Task LoadOccurrencesAsync()=>MutateAsync(()=>Task.CompletedTask);
    [RelayCommand] private Task SaveOccurrenceAsync()=>MutateAsync(async()=>
    {
        if(recurrence is null || occurrenceDetail is not { } item || editorProfile is not { } profile)return;
        await recurrence.UpdateAsync(new(profile,item.Occurrence.Id),global::PersonalWorkspace.App.ViewModels.RecurrenceEditor.ParseDate(OccurrenceDate),OccurrenceStatus,
            OccurrenceValue.ParseActual(),OccurrenceSkipped);
    });
    private void NotifyRecurrence()
    {
        foreach(var name in new[]{nameof(IsOccurrence),nameof(IsDefinitionEditor),nameof(IsOneOffDetail),nameof(CanEditDefinitionExecution),nameof(CanEditValueDefinition),
            nameof(IsSeries),nameof(ShowRecurrence),nameof(SeriesNotice),nameof(SeriesSummary),nameof(OccurrenceTitle),nameof(OccurrenceContext),nameof(OccurrenceRows),nameof(HasOccurrenceRows),
            nameof(ShowCarrySettings),nameof(CanEditCarrySettings),nameof(CarryPolicyNotice),nameof(OccurrenceBaseTarget),nameof(OccurrenceIncoming),nameof(OccurrenceEffectiveTarget),nameof(OccurrenceOutgoing),nameof(OccurrenceCarryNotice)})OnPropertyChanged(name);
    }
}
