using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record OrganizationFilter(Guid? Id, string Name);
public sealed record OrganizationChoice(WorkspaceItemReference Item, Guid Id, OrganizationKind Kind, string Name,
    OrganizationColor Color, bool IsAssigned, bool IsArchived = false)
{
    public string Label => (Kind == OrganizationKind.Tag ? "#" : "") + Name + (IsArchived ? " (archived)" : "");
    public string ActionLabel => IsAssigned ? "Remove" : "Assign";
    public string ActionDescription => $"{ActionLabel} {Label}";
}
