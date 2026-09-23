using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record ProfileRowViewModel(Profile Profile, bool IsCurrent)
{
    public string Name => Profile.Name;
    public string LastOpenedLocalText => Profile.LastOpenedAtUtc?
        .ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Never";
    public bool CanOpen => !IsCurrent;
    public string OpenLabel => IsCurrent ? "Current" : "Open";
}
