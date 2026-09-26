using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed class TaskValueEditor : ObservableObject
{
    private TaskValueType type;
    private string target = "", actual = "", currency = "", unit = "";
    public IReadOnlyList<TaskValueType> Types { get; } = Enum.GetValues<TaskValueType>();
    public TaskValueType Type
    {
        get => type;
        set
        {
            if (!SetProperty(ref type, value)) return;
            Target = value == TaskValueType.Percentage ? "100" : "";
            Actual = Unit = Currency = "";
            if (value == TaskValueType.Currency)
            {
                try { Currency = RegionInfo.CurrentRegion.ISOCurrencySymbol; }
                catch (ArgumentException) { }
            }
            Notify();
        }
    }
    public string Target { get => target; set { if (SetProperty(ref target, value)) Notify(); } }
    public string Actual { get => actual; set { if (SetProperty(ref actual, value)) Notify(); } }
    public string Currency { get => currency; set { if (SetProperty(ref currency, value)) Notify(); } }
    public string Unit { get => unit; set { if (SetProperty(ref unit, value)) Notify(); } }
    public bool HasValue => Type != TaskValueType.Checkbox;
    public bool IsCurrency => Type == TaskValueType.Currency;
    public bool IsCustom => Type == TaskValueType.CustomUnit;
    public string Hint => Type == TaskValueType.Duration ? "Use minutes:seconds or hours:minutes:seconds (30:00 or 1:15:30)."
        : "Use your Windows decimal separator. Leave Actual empty until a result is recorded.";
    public string Progress { get { try { return TaskValuePresentation.Progress(Build()); } catch (TaskValidationException) { return ""; } } }
    public double VisualProgress { get { try { return (double)(Build()?.VisualPercent ?? 0); } catch (TaskValidationException) { return 0; } } }
    public TaskValue? Build() => !HasValue ? null : new TaskValue(Type, TaskValuePresentation.Parse(Target, Type),
        string.IsNullOrWhiteSpace(Actual) ? null : TaskValuePresentation.Parse(Actual, Type), Currency, Unit).Validate();
    public decimal? ParseActual() => string.IsNullOrWhiteSpace(Actual) ? null : TaskValuePresentation.Parse(Actual, Type);
    public void Load(TaskValue? value)
    {
        Type = value?.Type ?? TaskValueType.Checkbox;
        Target = value is null ? "" : TaskValuePresentation.Input(value.Target, Type);
        Actual = value?.Actual is { } result ? TaskValuePresentation.Input(result, Type) : "";
        Currency = value?.CurrencyCode ?? ""; Unit = value?.Unit ?? "";
        Notify();
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(HasValue), nameof(IsCurrency), nameof(IsCustom), nameof(Hint), nameof(Progress), nameof(VisualProgress) }) OnPropertyChanged(name);
    }
}
