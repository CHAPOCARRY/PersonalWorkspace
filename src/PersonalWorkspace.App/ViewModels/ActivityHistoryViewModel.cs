using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record ActivityRow(AuditEvent Event, string Day)
{
    public string Time => Event.OccurredAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
    public string Summary => Event.Action switch
    {
        AuditAction.TemplateCaptured => $"Template captured: {Event.Target.Title}",
        AuditAction.TemplateUpdated => $"Template updated: {Event.Target.Title}",
        AuditAction.TemplateInstantiated => $"{ActivityHistoryViewModel.Words(Event.Target.Type.ToString())} created from Template: {Event.Target.Title}",
        _ => $"{ActivityHistoryViewModel.Words(Event.Target.Type.ToString())} {ActivityHistoryViewModel.Words(Event.Action.ToString()).ToLower(CultureInfo.CurrentCulture)}: {Event.Target.Title}"
    };
    public string Context => Event.Target.Date is { } date ? date.ToString("d", CultureInfo.CurrentCulture) : "";
    public override string ToString() => $"{Time} · {Summary}";
}
public sealed class ActivityHistoryViewModel : ObservableObject, IDisposable
{
    private readonly IActivityHistoryService service;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private CancellationTokenSource? pending;
    private long revision;
    private bool busy, canOpen;
    private string status = "", targetStatus = "", entityType = "All", action = "All", titlePrefix = "";
    private DateTimeOffset? from, through;
    private Guid? targetFilter;
    private Guid epoch;
    private AuditCursor? next;
    private IReadOnlyList<ActivityRow> rows = [];
    private ActivityRow? selected;
    private AuditDetails? details;
    public ActivityHistoryViewModel(IActivityHistoryService service, ICurrentProfile current, INavigationService navigation)
    {
        this.service = service; this.current = current; this.navigation = navigation;
        current.Changed += ProfileChanged; navigation.Changed += NavigationChanged;
    }
    public bool IsArea => navigation.Current.Destination == "History";
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool HasMore => next is not null;
    public bool HasMoreChanges => details?.HasMore == true;
    public bool CanOpen { get => canOpen; private set => SetProperty(ref canOpen, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string TargetStatus { get => targetStatus; private set => SetProperty(ref targetStatus, value); }
    public string EntityType { get => entityType; set => SetProperty(ref entityType, value); }
    public string Action { get => action; set => SetProperty(ref action, value); }
    public string TitlePrefix { get => titlePrefix; set => SetProperty(ref titlePrefix, value); }
    public DateTimeOffset? From { get => from; set => SetProperty(ref from, value); }
    public DateTimeOffset? Through { get => through; set => SetProperty(ref through, value); }
    public IReadOnlyList<string> EntityTypes { get; } = new[] { "All" }.Concat(Enum.GetNames<AuditEntityType>()).ToArray();
    public IReadOnlyList<string> Actions { get; } = new[] { "All" }.Concat(Enum.GetNames<AuditAction>()).ToArray();
    public IReadOnlyList<ActivityRow> Rows { get => rows; private set => SetProperty(ref rows, value); }
    public ActivityRow? Selected { get => selected; set { if (SetProperty(ref selected, value)) _ = SelectAsync(); } }
    public IReadOnlyList<string> Changes => details?.Changes.Select(c => $"{c.Target.Title} · {Words(c.Field.Length > 37 && Guid.TryParse(c.Field[..36], out _) ? c.Field[37..] : c.Field)}: {Format(c.Before)} → {Format(c.After)}").ToArray() ?? [];
    public string DetailContext => details is null ? "Select an activity to inspect its changes." : string.Join(" · ", details.Targets.Where(t => t.Role is "Parent" or "Source").Select(t => t.Title).Distinct());
    public string TargetFilterLabel => targetFilter is null ? "All targets" : "Filtered to selected target";
    public string OpenLabel => Selected?.Event.Target.Type is AuditEntityType.CanvasItem or AuditEntityType.Decoration or AuditEntityType.RichText or AuditEntityType.AssetReference ? "Open containing Page" : "Open target";
    public static string Words(string text) => Regex.Replace(text, "(?<=[a-z])([A-Z])", " $1");
    private static string Format(AuditValue value)
    {
        if (value.State == AuditValueState.Absent) return "not present";
        if (value.State == AuditValueState.Null) return "not set";
        if (value.State == AuditValueState.Omitted) return $"content omitted ({value.Length ?? 0:N0} characters)";
        var text = value.Value ?? "";
        if (value.Kind == AuditValueKind.Duration && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) text = ExactValueText.Duration(checked((long)seconds));
        else if (value.Kind == AuditValueKind.Decimal && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) text = ExactValueText.Input(number);
        else if (value.Kind == AuditValueKind.Date && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date)) text = date.ToString("d", CultureInfo.CurrentCulture);
        return value.State == AuditValueState.Truncated ? text + "… (truncated)" : text;
    }
    private void Cancel() { ++revision; pending?.Cancel(); pending?.Dispose(); pending = null; }
    private bool Fresh(long request, Guid profile) => request == revision && current.Current?.Id == profile && IsArea;
    private void ClearSelection() { selected = null; details = null; CanOpen = false; TargetStatus = ""; OnPropertyChanged(nameof(Selected)); DetailsChanged(); }
    private void DetailsChanged() { OnPropertyChanged(nameof(Changes)); OnPropertyChanged(nameof(DetailContext)); OnPropertyChanged(nameof(HasMoreChanges)); OnPropertyChanged(nameof(OpenLabel)); }
    private void ProfileChanged(object? sender, EventArgs args)
    {
        Cancel(); Rows = []; next = null; epoch = Guid.Empty; ClearSelection(); EntityType = Action = "All"; TitlePrefix = ""; From = Through = null; targetFilter = null;
        IsBusy = false; OnPropertyChanged(nameof(HasMore)); OnPropertyChanged(nameof(TargetFilterLabel)); if (IsArea) _ = RefreshAsync();
    }
    private void NavigationChanged(object? sender, EventArgs args)
    {
        Cancel(); IsBusy = false; OnPropertyChanged(nameof(IsArea));
        if (IsArea) { targetFilter = Guid.TryParse(navigation.Current.EntityId, out var id) ? id : null; OnPropertyChanged(nameof(TargetFilterLabel)); _ = RefreshAsync(); }
    }
    public async Task RefreshAsync(bool more = false)
    {
        Cancel(); if (!IsArea || current.Current is not { } profile) return;
        var request = revision; pending = new(); var token = pending.Token; IsBusy = true; ClearSelection();
        if (!more) { Rows = []; next = null; }
        Status = "Loading activity…";
        try
        {
            var query = new AuditQuery(From is { } f ? new DateTimeOffset(f.LocalDateTime.Date) : null,
                Through is { } t ? new DateTimeOffset(t.LocalDateTime.Date.AddDays(1)) : null,
                Enum.TryParse<AuditEntityType>(EntityType, out var type) ? type : null, Enum.TryParse<AuditAction>(Action, out var op) ? op : null,
                targetFilter, TitlePrefix.Trim(), more ? next : null);
            var page = await service.ReadAsync(profile.Id, query, token); if (!Fresh(request, profile.Id)) return;
            epoch = page.Epoch; next = page.Next;
            var events = (more ? Rows.Select(r => r.Event) : []).Concat(page.Events).ToArray();
            DateOnly? previous = null; var today = DateOnly.FromDateTime(DateTime.Now);
            Rows = events.Select(e => { var day = DateOnly.FromDateTime(e.OccurredAtUtc.LocalDateTime); var label = previous == day ? "" : day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("D", CultureInfo.CurrentCulture); previous = day; return new ActivityRow(e, label); }).ToArray();
            Status = Rows.Count == 0 ? "No activity matches these filters. History begins with changes made after this feature was installed." : $"{Rows.Count:N0} activities loaded";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Fresh(request, profile.Id)) Status = e is ActivityHistoryException or WorkspaceChangedException ? e.Message : "Activity history could not be loaded. Try again."; }
        finally { if (Fresh(request, profile.Id)) { IsBusy = false; OnPropertyChanged(nameof(HasMore)); } }
    }
    public async Task SelectAsync(bool more = false)
    {
        Cancel(); if (Selected is not { } row || current.Current?.Id != row.Event.ProfileId || !IsArea) { ClearSelection(); IsBusy = false; return; }
        var request = revision; pending = new(); var token = pending.Token; IsBusy = true; CanOpen = false;
        try
        {
            var result = await service.DetailsAsync(row.Event.ProfileId, row.Event.Sequence, more ? details?.Changes.LastOrDefault()?.Ordinal ?? -1 : -1, token);
            if (!Fresh(request, row.Event.ProfileId)) return;
            details = more && details is not null ? result with { Changes = details.Changes.Concat(result.Changes).ToArray() } : result; DetailsChanged();
            try { await service.ResolveAsync(row.Event.ProfileId, row.Event.Target, token); if (Fresh(request, row.Event.ProfileId)) { CanOpen = true; TargetStatus = "Target available"; } }
            catch (ActivityHistoryException e) { if (Fresh(request, row.Event.ProfileId)) TargetStatus = e.Message; }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Fresh(request, row.Event.ProfileId)) { details = null; DetailsChanged(); TargetStatus = e is ActivityHistoryException ? e.Message : "Activity details are unavailable."; } }
        finally { if (Fresh(request, row.Event.ProfileId)) IsBusy = false; }
    }
    public async Task OpenAsync()
    {
        if (Selected is not { } row || IsBusy || !CanOpen) return; var request = revision; IsBusy = true;
        try { var route = await service.ResolveAsync(row.Event.ProfileId, row.Event.Target); if (Fresh(request, row.Event.ProfileId)) navigation.Navigate(route); }
        catch (Exception e) { if (Fresh(request, row.Event.ProfileId)) { CanOpen = false; TargetStatus = e is ActivityHistoryException or WorkspaceChangedException ? e.Message : "This target is unavailable."; } }
        finally { if (request == revision) IsBusy = false; }
    }
    public void FilterSelected() { if (Selected is not { } row) return; targetFilter = row.Event.Target.Id; OnPropertyChanged(nameof(TargetFilterLabel)); _ = RefreshAsync(); }
    public void ResetFilters() { EntityType = Action = "All"; TitlePrefix = ""; From = Through = null; targetFilter = null; OnPropertyChanged(nameof(TargetFilterLabel)); _ = RefreshAsync(); }
    // Caller supplies the Profile/epoch captured BEFORE the confirmation dialog, preventing A→B→A clears.
    public (Guid Profile, Guid Epoch, long Revision)? ClearRequest => current.Current is { } p && IsIdle && epoch != Guid.Empty ? (p.Id, epoch, revision) : null;
    public async Task ClearConfirmedAsync(Guid profile, Guid expectedEpoch, long expectedRevision)
    {
        if (!Fresh(expectedRevision, profile) || epoch != expectedEpoch || !IsIdle) return;
        Cancel(); var request = revision; IsBusy = true;
        try { await service.ClearAsync(profile, expectedEpoch); if (Fresh(request, profile)) await RefreshAsync(); }
        catch (Exception e) { if (Fresh(request, profile)) Status = e is ActivityHistoryException ? e.Message : "History could not be cleared."; }
        finally { if (request == revision) IsBusy = false; }
    }
    public void Dispose() { Cancel(); current.Changed -= ProfileChanged; navigation.Changed -= NavigationChanged; }
}
