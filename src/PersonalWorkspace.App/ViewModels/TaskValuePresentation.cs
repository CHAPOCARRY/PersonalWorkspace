using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public static class TaskValuePresentation
{
    public static string Input(decimal value, TaskValueType type) => type == TaskValueType.Duration ? Duration((long)value) : ExactValueText.Input(value);
    public static string Duration(long seconds) => ExactValueText.Duration(seconds);
    public static decimal Parse(string text, TaskValueType type)
    {
        try { return ExactValueText.ParseInput(text, type == TaskValueType.Duration); }
        catch (FormatException exception) { throw new TaskValidationException(exception.Message); }
    }
    public static string Progress(TaskValue? value)
    {
        if (value is null) return "";
        var suffix = value.Type switch { TaskValueType.Percentage => "%", TaskValueType.Currency => " " + value.CurrencyCode, TaskValueType.CustomUnit => " " + value.Unit, _ => "" };
        var target = Input(value.Target, value.Type) + suffix;
        if (value.Actual is not { } actual) return (value.Target == 0 ? "Covered by previous surplus · Target: " : "No result recorded · Target: ") + target;
        var percent = decimal.Round(value.Percent!.Value, 0, MidpointRounding.AwayFromZero);
        return $"{Input(actual, value.Type)} / {target} · {percent:0}% of target";
    }

    public static string Amount(TaskValue value, decimal amount) => Input(amount,value.Type) + (value.Type switch
    {
        TaskValueType.Currency => " " + value.CurrencyCode,
        TaskValueType.CustomUnit => " " + value.Unit,
        TaskValueType.Percentage => "%",
        _ => ""
    });
}
