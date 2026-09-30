using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public enum TrackerChartKind { Line, Bar, Area }
public sealed record AnalyticsPresetChoice(AnalyticsPreset Value, string Label);
public sealed record AnalyticsMetric(string Name, string Value);
public sealed class AnalyticsComparisonChoice(TrackerItem tracker) : ObservableObject
{
    private bool selected;
    public TrackerItem Tracker { get; } = tracker;
    public string Title => Tracker.Item.Title;
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
}
public sealed partial class TrackerAnalyticsViewModel : ObservableObject
{
    private readonly ITrackerAnalyticsService service;
    private readonly ICurrentProfile current;
    private readonly TimeProvider clock;
    private WorkspaceItemReference? reference;
    private int revision;
    private bool busy, opened;
    private string? error;
    private AnalyticsPresetChoice preset;
    private DateTimeOffset? from, through;
    private TrackerChartKind chartKind;
    public TrackerAnalyticsViewModel(ITrackerAnalyticsService service, ICurrentProfile current, TimeProvider clock)
    {
        this.service = service; this.current = current; this.clock = clock; preset = Presets[1];
        current.Changed += (_, _) => { if (reference?.ProfileId != current.Current?.Id) Clear(); };
    }
    public IReadOnlyList<AnalyticsPresetChoice> Presets { get; } = new[]
    {
        new AnalyticsPresetChoice(AnalyticsPreset.SevenDays,"7 days"), new(AnalyticsPreset.ThirtyDays,"30 days"), new(AnalyticsPreset.NinetyDays,"90 days"),
        new(AnalyticsPreset.ThisMonth,"This month"), new(AnalyticsPreset.LastMonth,"Last month"), new(AnalyticsPreset.ThisYear,"This year"),
        new(AnalyticsPreset.AllTime,"All time"), new(AnalyticsPreset.Custom,"Custom")
    };
    public IReadOnlyList<TrackerChartKind> ChartKinds { get; } = Enum.GetValues<TrackerChartKind>();
    public AnalyticsPresetChoice Preset { get => preset; set { if (value is not null && SetProperty(ref preset, value)) { OnPropertyChanged(nameof(IsCustom)); if (!IsCustom && opened) _ = RefreshAsync(); } } }
    public DateTimeOffset? From { get => from; set => SetProperty(ref from, value); }
    public DateTimeOffset? Through { get => through; set => SetProperty(ref through, value); }
    public TrackerChartKind ChartKind { get => chartKind; set => SetProperty(ref chartKind, value); }
    public bool IsOpen { get => opened; set => SetProperty(ref opened, value); }
    public bool IsCustom => Preset.Value == AnalyticsPreset.Custom;
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy && reference is not null;
    public bool HasData => Result?.Series.Any(s => s.Summary.Count > 0) == true;
    public bool IsNumeric => Result?.Series.FirstOrDefault()?.Tracker.Settings.Type != TrackerValueType.Boolean;
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public TrackerAnalyticsResult? Result { get; private set; }
    public IReadOnlyList<AnalyticsMetric> Metrics { get; private set; } = [];
    public IReadOnlyList<AnalyticsComparisonChoice> Choices { get; private set; } = [];
    public string RangeLabel => Result is { } result ? $"{result.Range.From:d} – {result.Range.Through:d} · Period start dates" : "";
    public string EmptyText => Result is null ? "Choose a range and refresh analytics." : "No data in this range. Add an entry to start seeing trends.";
    public void Clear()
    {
        ++revision; reference = null; IsOpen = false; IsBusy = false; Result = null; Choices = []; Metrics = []; Error = null;
        preset = Presets[1]; From = Through = null; chartKind = TrackerChartKind.Line; Notify(); OnPropertyChanged(nameof(Preset)); OnPropertyChanged(nameof(IsCustom)); OnPropertyChanged(nameof(ChartKind));
    }
    public async Task SetTrackerAsync(WorkspaceItemReference? next)
    {
        if (reference != next)
        {
            Clear(); reference = next;
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            From = TrackerEditor.Picker(DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - 29))); Through = TrackerEditor.Picker(today);
        }
        if (opened) await RefreshAsync(); else Notify();
    }
    public async Task OpenAsync() { IsOpen = true; await RefreshAsync(); }
    [RelayCommand] public async Task RefreshAsync()
    {
        if (reference is not { } r) return;
        var request = ++revision; IsBusy = true; Error = null; Result = null; Metrics = []; Notify();
        var selected = Choices.Where(c => c.Selected).Select(c => c.Tracker.Item.Id).ToArray();
        try
        {
            if (selected.Length > 3) throw new TrackerValidationException("Choose at most three other Trackers (four in total).");
            var result = await service.GetAsync(r.ProfileId, new[] { r.ItemId }.Concat(selected).ToArray(), Preset.Value,
                TrackerEditor.LocalDate(From), TrackerEditor.LocalDate(Through));
            var choices = await service.GetCandidatesAsync(r.ProfileId);
            if (request != revision || reference != r) return;
            Result = result;
            Choices = choices.Where(t => t.Item.Id != r.ItemId).Select(t => new AnalyticsComparisonChoice(t) { Selected = selected.Contains(t.Item.Id) }).ToArray();
            Metrics = result.Series.SelectMany(MetricsFor).ToArray();
        }
        catch (Exception exception)
        {
            if (request == revision) Error = exception is TrackerValidationException or TrackerOperationException or WorkspaceChangedException
                ? exception.Message : "Analytics could not be loaded. Please try again.";
        }
        finally { if (request == revision) { IsBusy = false; Notify(); } }
    }
    private static IEnumerable<AnalyticsMetric> MetricsFor(TrackerAnalyticsSeries series)
    {
        var item = series.Tracker; var summary = series.Summary;
        string Amount(decimal? value) => value is null ? "—" : AnalyticsPresentation.Amount(value.Value, item.Settings);
        string Percent(decimal? value) => value is null ? "—" : $"{value:0.#}%";
        yield return new(item.Item.Title, TrackerPresentation.Frequency(item.Schedule));
        yield return new("Recorded period count", summary.Count.ToString());
        yield return new("Latest", summary.Latest is { } latest ? TrackerPresentation.Amount(latest, item.Settings) + (item.Target is { } goal ? " / " + TrackerPresentation.Amount(goal, item.Settings) : "") : "—");
        if (item.Settings.Type == TrackerValueType.Boolean)
        {
            yield return new("True / False periods", $"{summary.TrueCount} / {summary.FalseCount}");
            yield return new("True rate (recorded periods)", Percent(summary.CompletionRate));
        }
        else
        {
            yield return new("Average", Amount(summary.Average)); yield return new("Minimum", Amount(summary.Minimum)); yield return new("Maximum", Amount(summary.Maximum));
            if (summary.Total is not null) yield return new("Total of period values", Amount(summary.Total));
        }
        yield return new("Expected / Recorded / Missing", $"{series.Expected} / {series.Recorded} / {series.Missing}");
        yield return new("Recording rate", Percent(series.RecordingRate));
        yield return new(item.Settings.Type == TrackerValueType.Boolean ? "True streak · current / best" : "Recording streak · current / best", $"{series.Streak.Current} / {series.Streak.Best}");
        if (series.Target is { } target)
        {
            yield return new("Target reached / Expected", $"{target.Reached} / {target.Expected}");
            yield return new("Target hit rate", Percent(target.HitRate));
            if (target.AverageAttainment is not null) yield return new("Average attainment (recorded)", Percent(target.AverageAttainment));
            yield return new("Target streak · current / best", $"{target.Streak.Current} / {target.Streak.Best}");
        }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(Result), nameof(Metrics), nameof(Choices), nameof(IsIdle), nameof(HasData), nameof(IsNumeric), nameof(RangeLabel), nameof(EmptyText) }) OnPropertyChanged(name);
    }
}
public static class AnalyticsPresentation
{
    public static string Amount(decimal value, TrackerSettings settings) => settings.Type == TrackerValueType.Duration
        ? $"{decimal.Truncate(value / 3600):0}:{decimal.Truncate(value / 60) % 60:00}:{value % 60:00}"
        : TrackerPresentation.Amount(new(value), settings);
    public static string Point(TrackerAnalyticsSeries series, AnalyticsPoint point) => $"{series.Tracker.Item.Title} · {point.Date:d} · "
        + (point.Value is { } value ? TrackerPresentation.Amount(value, series.Tracker.Settings) : "Missing")
        + (series.Tracker.Target is { } target ? " · Target " + TrackerPresentation.Amount(target, series.Tracker.Settings) : "");
}
