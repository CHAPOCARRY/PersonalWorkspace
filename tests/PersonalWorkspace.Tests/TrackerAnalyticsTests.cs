using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private TrackerAnalyticsService Analytics(ITrackerAnalyticsRepository? repository = null) => new(repository ?? new SqliteTrackerRepository(), current, gate, clock, NullLogger<TrackerAnalyticsService>.Instance);
    private async Task<TrackerAnalyticsSeries> Analyze(TrackerItem item, DateOnly? from = null, DateOnly? through = null) =>
        Assert.Single((await Analytics().GetAsync(profile, [item.Item.Id], AnalyticsPreset.Custom, from ?? Day.AddDays(-29), through ?? Day)).Series);
    private static TrackerDraft Historical(TrackerValueType type = TrackerValueType.Decimal) => Draft(type) with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-29)) };

    [Fact]
    public async Task AnalyticsWeightUsesExactCanonicalValuesAndMissingIsNotZero()
    {
        var item = await Create(Historical());
        decimal[] values = [79.4m, 79m, 78.7m, 78.2m];
        for (var i = 0; i < values.Length; i++) await Save(item, values[i], Day.AddDays(i * 2 - 6));
        var result = await Analyze(item, Day.AddDays(-6));
        Assert.Equal(4, result.Summary.Count); Assert.Equal(78.2m, result.Summary.Latest!.Number);
        Assert.Equal(78.825m, result.Summary.Average); Assert.Equal(78.2m, result.Summary.Minimum); Assert.Equal(79.4m, result.Summary.Maximum);
        Assert.Null(result.Summary.Total); Assert.Equal(7, result.Expected); Assert.Equal(3, result.Missing);
        Assert.Equal(4 * 100m / 7, result.RecordingRate); Assert.Null(result.Points[1].Value);
        Assert.Equal(new AnalyticsStreak(1, 1), result.Streak); Assert.Null(result.Target);
    }
    [Theory]
    [InlineData(TrackerAggregation.Sum, "1.6")]
    [InlineData(TrackerAggregation.Average, "0.5333333333333333333333333333")]
    [InlineData(TrackerAggregation.Min, "0.4")]
    [InlineData(TrackerAggregation.Max, "0.7")]
    [InlineData(TrackerAggregation.Last, "0.4")]
    public async Task AnalyticsUsesAllFiveCanonicalAggregations(TrackerAggregation aggregation, string expected)
    {
        var item = await Create(Historical(TrackerValueType.CustomUnit) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = aggregation });
        await Save(item, .5m, hour: 10); await Save(item, .7m, hour: 11); await Save(item, .4m, hour: 12);
        var result = await Analyze(item, Day);
        var number = decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(number, result.Summary.Average); Assert.Equal(number, result.Points[0].Value!.Number);
        Assert.Equal(3, result.Points[0].EntryCount); Assert.Equal(1, result.Summary.Count);
        Assert.Equal(aggregation == TrackerAggregation.Sum ? number : null, result.Summary.Total);
        Assert.Equal((await service.GetPeriodAsync(Ref(item), Day)).Value, result.Points[0].Value);
    }
    [Fact]
    public async Task AnalyticsCorrectionDeletionAndAddingBackRebuildFromEntries()
    {
        var item = await Create(Historical()); var old = await Save(item, 10, Day.AddDays(-2)); await Save(item, 20);
        Assert.Equal(15m, (await Analyze(item)).Summary.Average);
        await Save(item, 12, old.LocalDate, old.Id); Assert.Equal(16m, (await Analyze(item)).Summary.Average);
        await service.DeleteEntryAsync(Ref(item), old.Id); Assert.Equal(20m, (await Analyze(item)).Summary.Average);
        await Save(item, 8, old.LocalDate); Assert.Equal(14m, (await Analyze(item)).Summary.Average);
        Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM TrackerEntries;"));
    }
    [Theory]
    [InlineData(AnalyticsPreset.SevenDays, "2026-09-23", "2026-09-29")]
    [InlineData(AnalyticsPreset.ThirtyDays, "2026-08-31", "2026-09-29")]
    [InlineData(AnalyticsPreset.NinetyDays, "2026-07-02", "2026-09-29")]
    [InlineData(AnalyticsPreset.ThisMonth, "2026-09-01", "2026-09-29")]
    [InlineData(AnalyticsPreset.LastMonth, "2026-08-01", "2026-08-31")]
    [InlineData(AnalyticsPreset.ThisYear, "2026-01-01", "2026-09-29")]
    public void AnalyticsPresetsHaveInclusiveLocalDateBounds(AnalyticsPreset preset, string start, string end) =>
        Assert.Equal(new(DateOnly.Parse(start), DateOnly.Parse(end)), AnalyticsRange.Resolve(preset, Day));
    [Fact]
    public void AnalyticsRangeHandlesYearLeapAndInvalidBoundaries()
    {
        Assert.Equal(new(new(2024, 2, 1), new(2024, 2, 29)), AnalyticsRange.Resolve(AnalyticsPreset.LastMonth, new(2024, 3, 3)));
        Assert.Equal(new(new(2025, 12, 1), new(2025, 12, 31)), AnalyticsRange.Resolve(AnalyticsPreset.LastMonth, new(2026, 1, 1)));
        Assert.Null(AnalyticsRange.Resolve(AnalyticsPreset.AllTime, Day));
        Assert.Throws<TrackerValidationException>(() => AnalyticsRange.Resolve(AnalyticsPreset.Custom, Day, Day, Day.AddDays(-1)));
        Assert.Throws<TrackerValidationException>(() => AnalyticsRange.Resolve(AnalyticsPreset.Custom, Day));
    }
    [Fact]
    public async Task AnalyticsRangeQueryExcludesOtherTrackersAndOutOfRangeHistory()
    {
        var item = await Create(Historical()); var other = await Create(Historical());
        await Save(item, 900, Day.AddDays(-29)); await Save(item, 10, Day.AddDays(-6)); await Save(item, 20);
        await Save(other, 888);
        var snapshot = await new SqliteTrackerRepository().ReadAnalyticsAsync(new(profile, paths.WorkspaceDatabase(profile)), [item.Item.Id], new(Day.AddDays(-6), Day), Day, default);
        Assert.Equal(2, snapshot.Entries.Count); Assert.All(snapshot.Entries, e => Assert.Equal(item.Item.Id, e.TrackerId));
        var shortRange = await Analytics().GetAsync(profile, [item.Item.Id], AnalyticsPreset.SevenDays);
        Assert.Equal(15m, shortRange.Series[0].Summary.Average);
        var all = await Analytics().GetAsync(profile, [item.Item.Id], AnalyticsPreset.AllTime);
        Assert.Equal(item.Schedule.StartDate, all.Range.From); Assert.Equal(3, all.Series[0].Summary.Count);
    }
    [Theory]
    [InlineData(TrackerFrequency.Daily, 7)]
    [InlineData(TrackerFrequency.Weekly, 1)]
    [InlineData(TrackerFrequency.EveryXDays, 3)]
    [InlineData(TrackerFrequency.SelectedWeekdays, 3)]
    [InlineData(TrackerFrequency.Monthly, 1)]
    [InlineData(TrackerFrequency.Unscheduled, 0)]
    public async Task AnalyticsExpectedPeriodsAndStreaksUseCanonicalSchedule(TrackerFrequency frequency, int expected)
    {
        var start = Day.AddDays(-6);
        var item = await Create(Draft(TrackerValueType.Integer) with { Schedule = new(frequency, start, Interval: 3, Weekdays: (1 << 1) | (1 << 3) | (1 << 5)) });
        for (var date = start; date <= Day; date = date.AddDays(1)) if (item.Schedule.PeriodOn(date) == date) await Save(item, 1, date);
        var result = await Analyze(item, start);
        Assert.Equal(expected, result.Expected); Assert.Equal(expected, result.Recorded); Assert.Equal(0, result.Missing);
        Assert.Equal(expected, result.Streak.Best); Assert.Equal(expected, result.Streak.Current);
        Assert.Equal(expected == 0 ? null : 100m, result.RecordingRate);
    }
    [Theory]
    [InlineData(TrackerFrequency.Weekly)] [InlineData(TrackerFrequency.EveryXDays)] [InlineData(TrackerFrequency.Monthly)]
    public async Task AnalyticsLongPeriodStreakAndMissingGap(TrackerFrequency frequency)
    {
        var start = frequency == TrackerFrequency.Monthly ? new DateOnly(2026, 6, 1) : Day.AddDays(frequency == TrackerFrequency.Weekly ? -21 : -9);
        var item = await Create(Draft() with { Schedule = new(frequency, start, Interval: 3) });
        var dates = Enumerable.Range(start.DayNumber, Day.DayNumber - start.DayNumber + 1).Select(DateOnly.FromDayNumber).Where(d => item.Schedule.PeriodOn(d) == d).ToArray();
        Assert.Equal(4, dates.Length);
        foreach (var date in dates) await Save(item, 1, date);
        Assert.Equal(new AnalyticsStreak(4, 4), (await Analyze(item, start)).Streak);
        var entry = Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: dates[1]))); await service.DeleteEntryAsync(Ref(item), entry.Id);
        Assert.Equal(new AnalyticsStreak(2, 2), (await Analyze(item, start)).Streak);
    }
    [Fact]
    public async Task AnalyticsMissingCurrentPeriodBreaksStreakUntilRecorded()
    {
        var item = await Create(Historical()); await Save(item, 1, Day.AddDays(-2)); await Save(item, 2, Day.AddDays(-1));
        Assert.Equal(new AnalyticsStreak(0, 2), (await Analyze(item)).Streak);
        await Save(item, 3); Assert.Equal(new AnalyticsStreak(3, 3), (await Analyze(item)).Streak);
        clock.Now = Day.AddDays(1);
        Assert.Equal(0, (await Analyze(item, through: clock.Now)).Streak.Current);
    }
    [Fact]
    public async Task AnalyticsBooleanHasPositiveStreakAndRecordedCompletionRate()
    {
        var item = await Create(Historical(TrackerValueType.Boolean)); bool[] values = [true, true, false, true];
        for (var i = 0; i < values.Length; i++) await service.SaveEntryAsync(Ref(item), Day.AddDays(i - 3), new(12, 0), new(Boolean: values[i]));
        var result = await Analyze(item, Day.AddDays(-3));
        Assert.Equal(3, result.Summary.TrueCount); Assert.Equal(1, result.Summary.FalseCount); Assert.Equal(75m, result.Summary.CompletionRate);
        Assert.Equal(new AnalyticsStreak(1, 2), result.Streak); Assert.Equal(100m, result.RecordingRate);
        Assert.Null(result.Summary.Average); Assert.Null(result.Summary.Total); Assert.True(result.Summary.Latest!.Boolean);
    }
    [Fact]
    public async Task AnalyticsTargetAndRecordingStreakAreSeparate()
    {
        var item = await Create(Historical(TrackerValueType.Integer) with { Target = new(10000) });
        int[] values = [10000, 11000, 9000, 8000, 12000, 13000, 14000];
        for (var i = 0; i < values.Length; i++) await Save(item, values[i], Day.AddDays(i - 6));
        var result = await Analyze(item, Day.AddDays(-6));
        Assert.Equal(new AnalyticsStreak(7, 7), result.Streak); Assert.Equal(new AnalyticsStreak(3, 3), result.Target!.Streak);
        Assert.Equal(5, result.Target.Reached); Assert.Equal(5 * 100m / 7, result.Target.HitRate);
        Assert.Equal(110m, result.Target.AverageAttainment); Assert.Equal(77000m, result.Summary.Total);
    }
    [Fact]
    public async Task AnalyticsLifecycleExcludesUnstartedEndedArchivedAndTrashedExpectations()
    {
        var ended = await Create(Historical() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-3), Day.AddDays(-1)), Target = new(1) });
        await Save(ended, 2, Day.AddDays(-2));
        var result = await Analyze(ended, Day.AddDays(-6)); Assert.Equal(3, result.Expected); Assert.Equal(0, result.Streak.Current); Assert.Equal(1, result.Target!.Reached);
        Assert.DoesNotContain(await service.GetAsync(profile, todayOnly: true), p => p.Tracker.Item.Id == ended.Item.Id);
        var future = await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(1)), Target = new(1) });
        Assert.Equal(0, (await Analyze(future, through: Day.AddDays(10))).Expected);
        var archived = await Create(Historical()); await Save(archived, 1, Day.AddDays(-1)); await service.ApplyAsync(Ref(archived), TrackerAction.Archive);
        result = await Analyze(archived, Day.AddDays(-1)); Assert.Equal(1, result.Expected); Assert.Equal(1, result.Summary.Count); Assert.Equal(0, result.Streak.Current);
        Assert.DoesNotContain(await Analytics().GetCandidatesAsync(profile), t => t.Item.Id == archived.Item.Id);
        await service.ApplyAsync(Ref(archived), TrackerAction.Trash);
        await Assert.ThrowsAsync<TrackerValidationException>(() => Analyze(archived));
        Assert.DoesNotContain(await Analytics().GetCandidatesAsync(profile), t => t.Item.Id == archived.Item.Id);
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM Trackers;"));
    }
    [Theory]
    [InlineData(TrackerValueType.Currency)] [InlineData(TrackerValueType.Distance)] [InlineData(TrackerValueType.CustomUnit)]
    public async Task AnalyticsExactFractionalTotalsAndAverages(TrackerValueType type)
    {
        var item = await Create(Historical(type)); await Save(item, .1m, Day.AddDays(-1)); await Save(item, .2m);
        var result = await Analyze(item); Assert.Equal(.15m, result.Summary.Average); Assert.Equal(.3m, result.Summary.Total);
        Assert.Equal(.1m, result.Summary.Minimum); Assert.Equal(.2m, result.Summary.Maximum);
    }
    [Fact]
    public async Task AnalyticsDurationKeepsWholeSecondsAndHumanFormatting()
    {
        var item = await Create(Historical(TrackerValueType.Duration)); await Save(item, 4530, Day.AddDays(-1)); await Save(item, 4531);
        var result = await Analyze(item); Assert.Equal(4531m, result.Summary.Average); Assert.Equal(9061m, result.Summary.Total);
        Assert.Equal("2:31:01", AnalyticsPresentation.Amount(result.Summary.Total!.Value, item.Settings));
    }
    [Theory]
    [InlineData(TrackerValueType.Scale, 1, 5)] [InlineData(TrackerValueType.Percentage, 0, 100)]
    public async Task AnalyticsBoundedTypesHaveMeaningfulSummariesWithoutTotal(TrackerValueType type, int min, int max)
    {
        var item = await Create(Historical(type) with { Settings = type == TrackerValueType.Scale ? new(type, ScaleMin: min, ScaleMax: max) : new(type) });
        await Save(item, min, Day.AddDays(-1)); await Save(item, max);
        var summary = (await Analyze(item)).Summary; Assert.Equal((min + max) / 2m, summary.Average); Assert.Equal(min, summary.Minimum); Assert.Equal(max, summary.Maximum); Assert.Null(summary.Total);
    }
    [Fact]
    public async Task AnalyticsHeatmapDistinguishesZeroMissingAndTarget()
    {
        var item = await Create(Historical(TrackerValueType.Integer) with { Target = new(1) }); await Save(item, 0, Day.AddDays(-2)); await Save(item, 1);
        var range = new AnalyticsRange(Day.AddDays(-2), Day); var result = await Analyze(item, range.From);
        var cells = TrackerAnalytics.Heatmap(result, range); Assert.Equal(3, cells.Count);
        Assert.Equal(0m, cells[0].Value!.Number); Assert.False(cells[0].TargetReached); Assert.Null(cells[1].Value); Assert.True(cells[1].Expected); Assert.True(cells[2].TargetReached);
        Assert.Equal(Day, cells[2].Date); Assert.Equal(Day, cells[2].PeriodDate);
        Assert.Equal(366, TrackerAnalytics.Heatmap(result, new(Day.AddYears(-3), Day)).Count);
    }
    [Fact]
    public async Task AnalyticsRangeSelectsCompleteWeeklyPeriodAndHeatmapRepeatsWithoutDoubleCounting()
    {
        var start = Day.AddDays(-6); var item = await Create(Draft(TrackerValueType.Integer) with { Schedule = new(TrackerFrequency.Weekly, start), EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Save(item, 2, start); await Save(item, 3, Day);
        var result = await Analyze(item, start, start); Assert.Equal(5m, result.Summary.Total); Assert.Equal(1, result.Summary.Count);
        Assert.Empty((await Analyze(item, start.AddDays(1))).Points);
        var cells = TrackerAnalytics.Heatmap(result, new(start, Day)); Assert.All(cells, c => Assert.Equal(5m, c.Value!.Number));
    }
    [Theory]
    [InlineData(2)] [InlineData(4)]
    public async Task AnalyticsComparisonUsesOneBatchAndPreservesOrder(int count)
    {
        var ids = new List<Guid>(); for (var i = 0; i < count; i++) { var item = await Create(); ids.Add(item.Item.Id); await Save(item, i + 1); }
        var repository = new AnalyticsProbe(new SqliteTrackerRepository());
        var result = await Analytics(repository).GetAsync(profile, ids, AnalyticsPreset.SevenDays);
        Assert.Equal(ids, result.Series.Select(s => s.Tracker.Item.Id)); Assert.Equal(1, repository.Reads);
        Assert.Equal(new(Day.AddDays(-6), Day), repository.LastRange); Assert.Equal(count, repository.Ids!.Count);
        Assert.Equal(12L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;"));
    }
    [Theory]
    [InlineData(TrackerValueType.Currency, "EUR", "USD", false)]
    [InlineData(TrackerValueType.Currency, "EUR", "EUR", true)]
    [InlineData(TrackerValueType.Distance, "km", "mi", false)]
    [InlineData(TrackerValueType.Distance, "km", "km", true)]
    [InlineData(TrackerValueType.CustomUnit, "pages", "repetitions", false)]
    [InlineData(TrackerValueType.CustomUnit, "L", "L", true)]
    public async Task AnalyticsComparisonRejectsIncompatibleUnitsWithoutConversion(TrackerValueType type, string a, string b, bool compatible)
    {
        TrackerSettings Settings(string unit) => type == TrackerValueType.Currency ? new(type, CurrencyCode: unit) : new(type, unit);
        var first = await Create(Draft(type) with { Settings = Settings(a) }); var second = await Create(Draft(type) with { Settings = Settings(b) });
        if (compatible) Assert.Equal(2, (await Analytics().GetAsync(profile, [first.Item.Id, second.Item.Id])).Series.Count);
        else await Assert.ThrowsAsync<TrackerValidationException>(() => Analytics().GetAsync(profile, [first.Item.Id, second.Item.Id]));
    }
    [Fact]
    public async Task AnalyticsComparisonRejectsTypesScaleBoundsDuplicatesAndExcessSelections()
    {
        var first = await Create(); var second = await Create(Draft(TrackerValueType.Integer));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Analytics().GetAsync(profile, [first.Item.Id, second.Item.Id]));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Analytics().GetAsync(profile, [first.Item.Id, first.Item.Id]));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Analytics().GetAsync(profile, Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.False(TrackerAnalytics.Compatible(new(TrackerValueType.Scale, ScaleMin: 1, ScaleMax: 5), new(TrackerValueType.Scale, ScaleMin: 1, ScaleMax: 10)));
        Assert.True(TrackerAnalytics.Compatible(new(TrackerValueType.Scale, ScaleMin: 1, ScaleMax: 5), new(TrackerValueType.Scale, ScaleMin: 1, ScaleMax: 5)));
    }
    [Fact]
    public async Task AnalyticsProfileSwitchClearsViewAndGuardsStaleQueries()
    {
        var item = await Create(); await Save(item, 78.2m);
        var model = new TrackerAnalyticsViewModel(Analytics(), current, clock); await model.SetTrackerAsync(Ref(item)); await model.OpenAsync();
        Assert.True(model.HasData); model.ChartKind = TrackerChartKind.Bar;
        var other = await profiles.CreateAsync("Analytics other");
        Assert.Null(model.Result); Assert.Empty(model.Metrics); Assert.Empty(model.Choices); Assert.False(model.IsOpen); Assert.Equal(TrackerChartKind.Line, model.ChartKind);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Analytics().GetAsync(profile, [item.Item.Id]));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Analytics().GetAsync(other.Id, [item.Item.Id]));
        await profiles.SwitchAsync(profile); Assert.Equal(78.2m, (await Analyze(item)).Summary.Latest!.Number);
    }
    [Fact]
    public async Task AnalyticsEmptyAndViewRefreshAfterCanonicalEdit()
    {
        var item = await Create(); var model = new TrackerAnalyticsViewModel(Analytics(), current, clock);
        await model.SetTrackerAsync(Ref(item)); await model.OpenAsync(); Assert.False(model.HasData); Assert.Contains("No data", model.EmptyText);
        Assert.Null(model.Result!.Series[0].Summary.Latest); Assert.Equal(1, model.Result.Series[0].Missing);
        await Save(item, 7); await model.SetTrackerAsync(Ref(item)); Assert.True(model.HasData); Assert.Equal(7m, model.Result!.Series[0].Summary.Latest!.Number);
        var next = await Create(); await model.SetTrackerAsync(Ref(next)); Assert.Null(model.Result); Assert.False(model.IsOpen);
    }
    [Fact]
    public async Task AnalyticsClockUsesLocalDateRatherThanUtcDate()
    {
        clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Analytics +14", TimeSpan.FromHours(14), "Analytics +14", "Analytics +14");
        var item = await Create(); await Save(item, 1, Day.AddDays(1));
        var result = await Analytics().GetAsync(profile, [item.Item.Id], AnalyticsPreset.SevenDays);
        Assert.Equal(Day.AddDays(1), result.Range.Through); Assert.Equal(1m, result.Series[0].Summary.Latest!.Number);
    }
    [Fact]
    public async Task AnalyticsQueriesDoNotWriteWorkspaceOrCreateAnalyticalTables()
    {
        var item = await Create(); await Save(item, 1);
        var before = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile));
        await Analyze(item); await Analytics().GetCandidatesAsync(profile);
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name LIKE '%Analytics%' OR name LIKE '%Streak%' OR name LIKE '%Heatmap%');"));
    }
    [Fact]
    public async Task AnalyticsMonthlySkipsShortMonthsInsteadOfInventingMissingPeriods()
    {
        var item = await Create(Draft() with { Schedule = new(TrackerFrequency.Monthly, new(2026, 1, 31)) });
        var result = await Analyze(item, new(2026, 1, 1));
        Assert.Equal(new[] { new DateOnly(2026, 1, 31), new(2026, 3, 31), new(2026, 5, 31), new(2026, 7, 31), new(2026, 8, 31) }, result.Points.Select(p => p.Date));
    }
    [Fact]
    public async Task AnalyticsFutureAndHistoricalWindowsHaveNoCurrentStreak()
    {
        var item = await Create(Historical()); await Save(item, 1, Day.AddDays(-1));
        Assert.Equal(new AnalyticsStreak(0, 1), (await Analyze(item, Day.AddDays(-1), Day.AddDays(-1))).Streak);
        var future = await Analyze(item, Day.AddDays(1), Day.AddDays(2)); Assert.Equal(0, future.Expected); Assert.Null(future.RecordingRate);
    }
    [Fact]
    public async Task AnalyticsBooleanMissingBreaksTrueStreakButDoesNotBecomeFalse()
    {
        var item = await Create(Historical(TrackerValueType.Boolean) with { Target = new(Boolean: true) });
        await service.SaveEntryAsync(Ref(item), Day.AddDays(-2), new(12, 0), new(Boolean: true));
        await service.SaveEntryAsync(Ref(item), Day, new(12, 0), new(Boolean: true));
        var result = await Analyze(item, Day.AddDays(-2)); Assert.Equal(new AnalyticsStreak(1, 1), result.Streak);
        Assert.Equal(0, result.Summary.FalseCount); Assert.Equal(100m, result.Summary.CompletionRate);
        Assert.Equal(2 * 100m / 3, result.Target!.HitRate); Assert.Null(result.Target.AverageAttainment);
    }
    [Fact]
    public async Task AnalyticsLargeExactAverageAvoidsIntermediateDecimalOverflow()
    {
        var item = await Create(Historical()); await Save(item, decimal.MaxValue, Day.AddDays(-1)); await Save(item, decimal.MaxValue);
        Assert.Equal(decimal.MaxValue, (await Analyze(item)).Summary.Average);
    }
    [Fact]
    public async Task AnalyticsFailedComparisonClearsOldResultAndRetainsChoicesForCorrection()
    {
        var first = await Create(); var second = await Create(Draft(TrackerValueType.Currency)); await Save(first, 1);
        var model = new TrackerAnalyticsViewModel(Analytics(), current, clock); await model.SetTrackerAsync(Ref(first)); await model.OpenAsync();
        Assert.True(model.HasData); Assert.Single(model.Choices).Selected = true; await model.RefreshAsync();
        Assert.Null(model.Result); Assert.False(model.HasData); Assert.Contains("same value type", model.Error);
        Assert.Single(model.Choices).Selected = false; await model.RefreshAsync(); Assert.Null(model.Error); Assert.True(model.HasData);
    }
    [Fact]
    public async Task AnalyticsStaleCompletionCannotRepopulateAfterProfileSwitch()
    {
        var item = await Create(); await Save(item, 1); var result = await Analytics().GetAsync(profile, [item.Item.Id]);
        var delayed = new DelayedAnalytics(); var model = new TrackerAnalyticsViewModel(delayed, current, clock);
        await model.SetTrackerAsync(Ref(item)); var load = model.OpenAsync(); Assert.True(model.IsBusy);
        await profiles.CreateAsync("Delayed analytics other"); delayed.Completion.SetResult(result); await load;
        Assert.Null(model.Result); Assert.Empty(model.Choices); Assert.Empty(model.Metrics); Assert.False(model.IsBusy);
    }
    [Fact]
    public async Task BooleanSelectorClearsToNullAndCanSaveConsecutiveTrueEntries()
    {
        var item = await Create(Historical(TrackerValueType.Boolean)); var navigation = new NavigationService(); navigation.Navigate(new("Tracker", item.Item.Id.ToString("D")));
        var model = new TrackerWorkspaceViewModel(service, organization, current, navigation, clock, NullLogger<TrackerWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); Assert.Null(model.BooleanEntryValue);
        model.EntryDate = TrackerEditor.Picker(Day.AddDays(-1)); model.BooleanEntryValue = "true"; await model.SaveEntryCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Null(model.BooleanEntryValue);
        model.EntryDate = TrackerEditor.Picker(Day); model.BooleanEntryValue = "true"; await model.SaveEntryCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(2, (await Analyze(item)).Summary.TrueCount);
    }
    private sealed class DelayedAnalytics : ITrackerAnalyticsService
    {
        public TaskCompletionSource<TrackerAnalyticsResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrackerItem>>([]);
        public Task<TrackerAnalyticsResult> GetAsync(Guid profileId, IReadOnlyList<Guid> ids, AnalyticsPreset preset = AnalyticsPreset.ThirtyDays, DateOnly? from = null, DateOnly? through = null, CancellationToken cancellationToken = default) => Completion.Task;
    }
    private sealed class AnalyticsProbe(ITrackerAnalyticsRepository inner) : ITrackerAnalyticsRepository
    {
        public int Reads { get; private set; }
        public AnalyticsRange? LastRange { get; private set; }
        public IReadOnlyList<Guid>? Ids { get; private set; }
        public Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => inner.GetCandidatesAsync(workspace, cancellationToken);
        public Task<AnalyticsSnapshot> ReadAnalyticsAsync(WorkspaceContext workspace, IReadOnlyList<Guid> ids, AnalyticsRange? range, DateOnly today, CancellationToken cancellationToken)
        { Reads++; LastRange = range; Ids = ids; return inner.ReadAnalyticsAsync(workspace, ids, range, today, cancellationToken); }
    }
}
