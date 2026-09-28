using System.Numerics;

namespace PersonalWorkspace.Core;

public sealed record CarrySettings(bool Deficit = false, bool Surplus = false, bool Locked = false)
{
    public bool Enabled => Deficit || Surplus;
    public static bool Supports(TaskValueType type) => type is TaskValueType.Number or TaskValueType.Currency or TaskValueType.Duration or TaskValueType.CustomUnit;
}

public sealed record OccurrenceCalculation(decimal BaseTarget, decimal CarryIn, decimal EffectiveTarget, decimal CarryOut)
{
    public static OccurrenceCalculation Initial(decimal target) => new(target, 0, target, 0);
    public static OccurrenceCalculation Calculate(decimal basis, decimal incoming, decimal? actual, CarrySettings policy, bool skipped)
    {
        if (skipped) return new(basis, 0, 0, 0);
        // Credit covers only this execution. Discard any excess before calculating its result.
        incoming = policy.Enabled ? Math.Max(-basis, incoming) : 0;
        var effective = ExactDecimal.Add(basis, incoming);
        var outgoing = actual is { } result && (result < effective && policy.Deficit || result > effective && policy.Surplus)
            ? ExactDecimal.Subtract(effective,result) : 0;
        return new(basis, incoming, effective, outgoing);
    }
}

// Decimal addition can silently round at the edge of its precision. Reject such carry writes instead.
public static class ExactDecimal
{
    public static decimal Add(decimal left, decimal right) => Combine(left, right, false);
    public static decimal Subtract(decimal left, decimal right) => Combine(left, right, true);
    private static decimal Combine(decimal left, decimal right, bool subtract)
    {
        static (BigInteger Number, int Scale) Parts(decimal value)
        {
            var bits = decimal.GetBits(value);
            var number = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
            return ((bits[3] & int.MinValue) == 0 ? number : -number, (bits[3] >> 16) & 255);
        }
        var a = Parts(left); var b = Parts(right); var scale = Math.Max(a.Scale, b.Scale);
        var number = a.Number * BigInteger.Pow(10, scale - a.Scale) + (subtract ? -b.Number : b.Number) * BigInteger.Pow(10, scale - b.Scale);
        while (scale > 0 && number % 10 == 0) { number /= 10; scale--; }
        var negative = number.Sign < 0; number = BigInteger.Abs(number);
        if (number > (BigInteger.One << 96) - 1)
            throw new TaskValidationException("This carry calculation exceeds the supported exact decimal range. Reduce the values before saving.");
        return new((int)(uint)(number & uint.MaxValue), (int)(uint)((number >> 32) & uint.MaxValue), (int)(uint)(number >> 64), negative, (byte)scale);
    }
}

public static class RecurrenceSchedule
{
    // Find a conceptual slot arithmetically, without materializing or walking intervening days.
    public static DateOnly? Next(RecurrenceSegment segment, DateOnly from, DateOnly through)
    {
        var rule = segment.Rule;
        var first = Math.Max(from.DayNumber, Math.Max(rule.StartDate.DayNumber, segment.From.DayNumber));
        var last = Math.Min(through.DayNumber, Math.Min(rule.EndDate?.DayNumber ?? DateOnly.MaxValue.DayNumber,
            segment.Until is { } until ? until.DayNumber - 1 : DateOnly.MaxValue.DayNumber));
        if (!segment.Enabled || first > last) return null;
        if (rule.Pattern == RecurrencePattern.Daily)
        {
            var delta = first - rule.StartDate.DayNumber;
            var day = rule.StartDate.DayNumber + ((delta + rule.Interval - 1) / rule.Interval) * rule.Interval;
            return day <= last ? DateOnly.FromDayNumber(day) : null;
        }
        if (rule.Pattern == RecurrencePattern.Weekly)
        {
            var monday = rule.StartDate.DayNumber - ((int)rule.StartDate.DayOfWeek + 6) % 7;
            var week = (first - monday) / 7;
            week = ((week + rule.Interval - 1) / rule.Interval) * rule.Interval;
            for (var start = monday + week * 7; start <= last; start += rule.Interval * 7)
                for (var offset = 0; offset < 7; offset++)
                    if (start + offset >= first && start + offset <= last && (rule.Weekdays & (1 << ((offset + 1) % 7))) != 0)
                        return DateOnly.FromDayNumber(start + offset);
            return null;
        }
        var date = DateOnly.FromDayNumber(first); var anchor = (rule.StartDate.Year - 1) * 12 + rule.StartDate.Month - 1;
        var month = (date.Year - 1) * 12 + date.Month - 1;
        month = anchor + ((month - anchor + rule.Interval - 1) / rule.Interval) * rule.Interval;
        var end = DateOnly.FromDayNumber(last); var lastMonth = (end.Year - 1) * 12 + end.Month - 1;
        for (; month <= lastMonth; month += rule.Interval)
        {
            var year = month / 12 + 1; var number = month % 12 + 1; var length = DateTime.DaysInMonth(year, number);
            var day = rule.MonthDay;
            if (rule.Pattern == RecurrencePattern.MonthlyWeekday)
            {
                var edge = new DateOnly(year, number, rule.Ordinal == -1 ? length : 1);
                day = rule.Ordinal == -1 ? length - ((int)edge.DayOfWeek - (int)rule.Weekday + 7) % 7
                    : 1 + ((int)rule.Weekday - (int)edge.DayOfWeek + 7) % 7 + (rule.Ordinal - 1) * 7;
            }
            if (day > length) continue;
            var candidate = new DateOnly(year, number, day);
            if (candidate.DayNumber >= first && candidate.DayNumber <= last) return candidate;
        }
        return null;
    }
}
