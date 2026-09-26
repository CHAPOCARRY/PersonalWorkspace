using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public static class TaskValuePresentation
{
    public static string Input(decimal value, TaskValueType type) => type == TaskValueType.Duration ? Duration((long)value)
        : value.ToString("0.############################", CultureInfo.CurrentCulture);

    public static string Duration(long seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    public static decimal Parse(string text, TaskValueType type)
    {
        text = text.Trim();
        if (type != TaskValueType.Duration)
        {
            if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value)) return value;
            throw new TaskValidationException("Enter a decimal number using your Windows decimal separator, without thousands separators.");
        }
        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(part => part.Length > 0 && part.All(c => c is >= '0' and <= '9')))
        {
            try
            {
                var numbers = parts.Select(part => long.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                if (numbers[^1] < 60 && (parts.Length == 2 || numbers[1] < 60))
                {
                    var seconds = parts.Length == 2 ? checked(numbers[0] * 60 + numbers[1]) : checked(numbers[0] * 3600 + numbers[1] * 60 + numbers[2]);
                    if (seconds <= TaskValue.MaximumDurationSeconds) return seconds;
                }
            }
            catch (OverflowException) { }
        }
        throw new TaskValidationException("Enter duration as minutes:seconds or hours:minutes:seconds, such as 30:00 or 1:15:30.");
    }

    public static string Progress(TaskValue? value)
    {
        if (value is null) return "";
        var suffix = value.Type switch { TaskValueType.Percentage => "%", TaskValueType.Currency => " " + value.CurrencyCode, TaskValueType.CustomUnit => " " + value.Unit, _ => "" };
        var target = Input(value.Target, value.Type) + suffix;
        if (value.Actual is not { } actual) return "No result recorded · Target: " + target;
        var percent = decimal.Round(value.Percent!.Value, 0, MidpointRounding.AwayFromZero);
        return $"{Input(actual, value.Type)} / {target} · {percent:0}% of target";
    }
}
