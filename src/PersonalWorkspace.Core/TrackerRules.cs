using System.Numerics;

namespace PersonalWorkspace.Core;

public static class TrackerRules
{
    public const long MaximumDurationSeconds = long.MaxValue / TimeSpan.TicksPerSecond;
    public static TrackerDraft Validate(TrackerDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new TrackerValidationException("Enter a Tracker name.");
        var settings = draft.Settings with { Unit = Clean(draft.Settings.Unit), CurrencyCode = Clean(draft.Settings.CurrencyCode)?.ToUpperInvariant() };
        if (!Enum.IsDefined(settings.Type) || !Enum.IsDefined(draft.EntryMode) || !Enum.IsDefined(draft.Aggregation))
            throw new TrackerValidationException("Choose valid Tracker settings.");
        if (settings.Type == TrackerValueType.Currency && (settings.CurrencyCode is not { Length: 3 } code || code.Any(c => c is < 'A' or > 'Z')))
            throw new TrackerValidationException("Enter a three-letter currency code, such as EUR.");
        if (settings.Type == TrackerValueType.Distance && settings.Unit is not ("m" or "km" or "mi"))
            throw new TrackerValidationException("Choose a distance unit: m, km or mi.");
        if (settings.Type == TrackerValueType.CustomUnit && settings.Unit is null)
            throw new TrackerValidationException("Enter a short unit.");
        if (settings.Unit is { } unit && (unit.Length > 32 || unit.Any(char.IsControl)))
            throw new TrackerValidationException("Use a unit of at most 32 characters without control characters.");
        if (settings.Type == TrackerValueType.Scale && (settings.ScaleMin is null || settings.ScaleMax is null || settings.ScaleMax <= settings.ScaleMin))
            throw new TrackerValidationException("Scale maximum must be greater than its minimum.");
        settings = settings with
        {
            Unit = settings.Type is TrackerValueType.Decimal or TrackerValueType.Integer or TrackerValueType.CustomUnit or TrackerValueType.Distance ? settings.Unit : null,
            CurrencyCode = settings.Type == TrackerValueType.Currency ? settings.CurrencyCode : null,
            ScaleMin = settings.Type == TrackerValueType.Scale ? settings.ScaleMin : null,
            ScaleMax = settings.Type == TrackerValueType.Scale ? settings.ScaleMax : null
        };
        var schedule = draft.Schedule;
        if (!Enum.IsDefined(schedule.Frequency)) throw new TrackerValidationException("Choose a valid frequency.");
        if (schedule.Frequency != TrackerFrequency.Unscheduled && schedule.StartDate is null)
            throw new TrackerValidationException("Choose a start date for scheduled tracking.");
        if (schedule.EndDate < schedule.StartDate) throw new TrackerValidationException("End date cannot be before start date.");
        if (schedule.Frequency == TrackerFrequency.EveryXDays && schedule.Interval is < 1 or > 999)
            throw new TrackerValidationException("Enter an interval from 1 to 999 days.");
        if (schedule.Frequency == TrackerFrequency.SelectedWeekdays && schedule.Weekdays is < 1 or > 127)
            throw new TrackerValidationException("Select at least one weekday.");
        schedule = schedule with { Interval = schedule.Frequency == TrackerFrequency.EveryXDays ? schedule.Interval : 1,
            Weekdays = schedule.Frequency == TrackerFrequency.SelectedWeekdays ? schedule.Weekdays : 0 };
        var aggregation = draft.EntryMode == TrackerEntryMode.Single ? TrackerAggregation.Last : draft.Aggregation;
        if (!Aggregations(settings.Type).Contains(aggregation)) throw new TrackerValidationException("Choose an aggregation compatible with this value type.");
        if (draft.Target is { } target) ValidateValue(settings, target);
        return draft with { Title = draft.Title.Trim(), Settings = settings, Schedule = schedule, Aggregation = aggregation };
    }
    public static IReadOnlyList<TrackerAggregation> Aggregations(TrackerValueType type) => type switch
    {
        TrackerValueType.Boolean => [TrackerAggregation.Last],
        TrackerValueType.Scale or TrackerValueType.Percentage => [TrackerAggregation.Average, TrackerAggregation.Min, TrackerAggregation.Max, TrackerAggregation.Last],
        _ => Enum.GetValues<TrackerAggregation>()
    };
    public static void ValidateValue(TrackerSettings settings, TrackerValue value)
    {
        if (settings.Type == TrackerValueType.Boolean)
        {
            if (value.Boolean is null || value.Number is not null) throw new TrackerValidationException("Choose true or false.");
            return;
        }
        if (value.Number is not { } number || value.Boolean is not null) throw new TrackerValidationException("Enter a numeric value.");
        if (settings.Type is TrackerValueType.Integer or TrackerValueType.Scale && (number != decimal.Truncate(number) || number < long.MinValue || number > long.MaxValue))
            throw new TrackerValidationException("Enter a whole number in the supported integer range.");
        if (settings.Type == TrackerValueType.Percentage && (number < 0 || number > 100)) throw new TrackerValidationException("Percentage must be between 0 and 100.");
        if (settings.Type == TrackerValueType.Duration && (number < 0 || number > MaximumDurationSeconds || number != decimal.Truncate(number)))
            throw new TrackerValidationException("Duration must be whole seconds within the supported range.");
        if (settings.Type == TrackerValueType.Distance && number < 0) throw new TrackerValidationException("Distance cannot be negative.");
        if (settings.Type == TrackerValueType.Scale && (number < settings.ScaleMin || number > settings.ScaleMax))
            throw new TrackerValidationException($"Enter a rating from {settings.ScaleMin} to {settings.ScaleMax}.");
    }
    public static TrackerValue? Aggregate(TrackerItem tracker, IEnumerable<TrackerEntry> source)
    {
        var entries = source.OrderBy(e => e.LocalDate).ThenBy(e => e.LocalTime).ThenBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).ToArray();
        if (entries.Length == 0) return null;
        if (tracker.EntryMode == TrackerEntryMode.Single || tracker.Aggregation == TrackerAggregation.Last) return entries[^1].Value;
        var values = entries.Select(e => e.Value.Number!.Value).ToArray();
        var result = tracker.Aggregation switch
        {
            TrackerAggregation.Min => values.Min(), TrackerAggregation.Max => values.Max(),
            TrackerAggregation.Sum => SumOrAverage(values, false), TrackerAggregation.Average => SumOrAverage(values, true),
            _ => throw new TrackerValidationException("This aggregation is not supported.")
        };
        // Duration summaries retain whole seconds; canonical entries are never rounded.
        if (tracker.Settings.Type == TrackerValueType.Duration && tracker.Aggregation == TrackerAggregation.Average)
            result = decimal.Round(result, 0, MidpointRounding.AwayFromZero);
        if (tracker.Settings.Type == TrackerValueType.Duration && result > MaximumDurationSeconds)
            throw new TrackerValidationException("The period duration exceeds the supported range.");
        return new(result);
    }
    public static decimal SumOrAverage(decimal[] values, bool average)
    {
        if (values.Length == 0) throw new ArgumentException("At least one value is required.", nameof(values));
        // Accumulate integer coefficients at scale 28. This avoids order-dependent rounding
        // and intermediate overflow (including an average of several decimal.MaxValue values).
        BigInteger sum = 0;
        foreach (var value in values)
        {
            var bits = decimal.GetBits(value); var scale = (bits[3] >> 16) & 255;
            var coefficient = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
            sum += ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient) * BigInteger.Pow(10, 28 - scale);
        }
        var divisor = average ? values.Length : 1; var negative = sum.Sign < 0; sum = BigInteger.Abs(sum);
        for (var scale = 28; scale >= 0; scale--)
        {
            var denominator = divisor * BigInteger.Pow(10, 28 - scale);
            var coefficient = BigInteger.DivRem(sum, denominator, out var remainder);
            if (average && remainder * 2 >= denominator) coefficient++;
            if (coefficient > (BigInteger.One << 96) - 1) continue;
            if (!average && remainder != 0) break;
            return new((int)(uint)(coefficient & uint.MaxValue), (int)(uint)((coefficient >> 32) & uint.MaxValue),
                (int)(uint)(coefficient >> 64), negative, (byte)scale);
        }
        throw new TrackerValidationException("The period total exceeds the supported exact decimal range.");
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
