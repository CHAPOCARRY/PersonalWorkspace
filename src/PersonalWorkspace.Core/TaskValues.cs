namespace PersonalWorkspace.Core;

public enum TaskValueType { Checkbox, Number, Percentage, Currency, Duration, CustomUnit }

// Target is the base definition; Actual is the optional current one-off result.
// Duration uses whole total seconds in these decimal fields and INTEGER storage.
public sealed record TaskValue(TaskValueType Type, decimal Target, decimal? Actual = null, string? CurrencyCode = null, string? Unit = null)
{
    public const long MaximumDurationSeconds = long.MaxValue / TimeSpan.TicksPerSecond;
    public bool IsReached => Actual is { } actual && actual >= Target;
    public decimal? Ratio => Actual / Target;
    public decimal? Percent => Ratio * 100m;
    public decimal VisualPercent => Math.Min(100m, Percent ?? 0m);

    public TaskValue Validate()
    {
        if (!Enum.IsDefined(Type) || Type == TaskValueType.Checkbox)
            throw new TaskValidationException("Select a valid value type. Checkbox tasks have no value configuration.");
        if (Target <= 0) throw new TaskValidationException("Target must be greater than zero.");
        if (Actual < 0) throw new TaskValidationException("Actual cannot be negative.");
        if (Type == TaskValueType.Percentage && (Target > 100 || Actual > 100))
            throw new TaskValidationException("Percentage target and actual cannot exceed 100%.");
        if (Type == TaskValueType.Duration && (Target != decimal.Truncate(Target) || Target > MaximumDurationSeconds
            || Actual is { } seconds && (seconds != decimal.Truncate(seconds) || seconds > MaximumDurationSeconds)))
            throw new TaskValidationException("Enter a duration in whole seconds within the supported duration range.");
        var currency = CurrencyCode?.Trim().ToUpperInvariant();
        if (Type == TaskValueType.Currency && (currency is not { Length: 3 } || currency.Any(c => c is < 'A' or > 'Z')))
            throw new TaskValidationException("Enter a three-letter currency code, such as EUR or USD.");
        var unit = Unit?.Trim();
        if (Type == TaskValueType.CustomUnit && (string.IsNullOrWhiteSpace(unit) || unit.Length > 32 || unit.Any(char.IsControl)))
            throw new TaskValidationException("Enter a unit label of 1 to 32 characters, such as pages or km.");
        try { _ = Percent; }
        catch (OverflowException) { throw new TaskValidationException("These values are too far apart to calculate progress. Use a larger target or smaller actual."); }
        return this with { CurrencyCode = Type == TaskValueType.Currency ? currency : null, Unit = Type == TaskValueType.CustomUnit ? unit : null };
    }
}
