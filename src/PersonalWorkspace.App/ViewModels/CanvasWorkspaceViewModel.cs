using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record CanvasContainerChoice(Guid? Id, string Label);
public sealed class CanvasWorkspaceViewModel : ObservableObject
{
    private readonly ICanvasService service;
    private readonly ICurrentProfile current;
    private readonly ILogger<CanvasWorkspaceViewModel> logger;
    private int revision;
    private bool loading, saving;
    private CanvasSnapshot? gestureStart;
    private CanvasEdit? previewEdit;
    public CanvasWorkspaceViewModel(ICanvasService service, ICurrentProfile current, ILogger<CanvasWorkspaceViewModel> logger, WidgetWorkspaceViewModel? widgets = null)
    {
        this.service = service; this.current = current; this.logger = logger;
        Widgets = widgets;
        if (widgets is not null)
        {
            widgets.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(WidgetWorkspaceViewModel.IsBusy)) Notify(); };
            widgets.LayoutChanged += snapshot => { if (Reference?.ItemId == snapshot.PageId) { Snapshot = snapshot; DisplayItems = snapshot.Items; Selection.IntersectWith(snapshot.Items.Select(i => i.Id)); Notify(); } };
        }
        current.Changed += (_, _) => { if (Reference?.ProfileId != current.Current?.Id) Clear(); };
    }
    public WorkspaceItemReference? Reference { get; private set; }
    public WidgetWorkspaceViewModel? Widgets { get; }
    public CanvasSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<CanvasItem> DisplayItems { get; private set; } = [];
    public HashSet<Guid> Selection { get; } = [];
    public bool IsBusy => loading || saving || Widgets?.IsBusy == true;
    public bool HasPage => Snapshot is not null;
    public bool CanEdit => !IsBusy && Snapshot is { PageActive: true, Settings.LayoutLocked: false };
    public bool CanConfigure => !IsBusy && Snapshot?.PageActive == true;
    public bool Locked => Snapshot?.Settings.LayoutLocked ?? true;
    public int Zoom => Snapshot?.Settings.ZoomPercent ?? 100;
    public bool IsPreview => gestureStart is not null;
    public bool PreviewValid { get; private set; } = true;
    public CanvasSnap? Guides { get; private set; }
    public string? Error { get; private set; }
    public string Status => Error ?? (IsPreview ? PreviewValid ? "Release to save" : "Invalid placement — release to restore" : saving ? "Saving layout…" : Selection.Count > 0 ? $"{Selection.Count} selected" : Locked ? "Layout locked" : "Ctrl+click to select several items · drag to move");
    public IReadOnlyList<CanvasContainerChoice> Containers => new[] { new CanvasContainerChoice(null, "Page canvas") }.Concat((Snapshot?.Items ?? []).Where(i => i.Kind == CanvasKind.Container).Select((i, n) => new CanvasContainerChoice(i.Id, i.Title.Length > 0 ? i.Title : $"Container {n + 1}"))).ToArray();
    public async Task OpenAsync(WorkspaceItemReference? reference, bool force = false)
    {
        if (!force && reference == Reference && Snapshot is not null) return;
        Clear(); if (reference is null || reference.ProfileId != current.Current?.Id) return;
        var request = revision; Reference = reference; loading = true; Notify();
        try { var snapshot = await service.GetAsync(reference); if (request == revision) { Snapshot = snapshot; DisplayItems = snapshot.Items; if (Widgets is not null) await Widgets.OpenAsync(reference); } }
        catch (Exception exception) { if (request == revision) Report(exception); }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public void Clear()
    {
        ++revision; Reference = null; Snapshot = null; DisplayItems = []; Selection.Clear(); loading = false; gestureStart = null; previewEdit = null; Guides = null; PreviewValid = true; Error = null; Notify();
        Widgets?.Clear();
    }
    public void Select(Guid? id, bool toggle = false)
    {
        if (IsBusy || IsPreview) return; Error = null;
        if (id is null) Selection.Clear();
        else if (Snapshot?.Items.SingleOrDefault(i => i.Id == id) is { } item)
        {
            var sameParent = Snapshot.Items.Where(i => Selection.Contains(i.Id)).All(i => i.ParentContainerId == item.ParentContainerId);
            if (!toggle || !sameParent) Selection.Clear();
            if (!Selection.Add(id.Value) && toggle) Selection.Remove(id.Value);
        }
        Notify();
    }
    public bool BeginGesture(Guid id)
    {
        if (!CanEdit || Snapshot is null || !Snapshot.Items.Any(i => i.Id == id)) return false;
        if (!Selection.Contains(id)) Select(id); gestureStart = Snapshot; previewEdit = null; Error = null; return true;
    }
    public void PreviewMove(double dx, double dy)
    {
        if (gestureStart is null) return;
        try { Guides = CanvasSnapping.Move(gestureStart, Selection, dx, dy); Preview(new MoveCanvasItems(Selection.ToArray(), Guides.DeltaX, Guides.DeltaY)); }
        catch (CanvasValidationException exception) { PreviewValid = false; Error = exception.Message; Notify(); }
    }
    public void PreviewResize(Guid id, double width, double height)
    {
        if (gestureStart is null || Selection.Count != 1) return;
        try { Guides = null; Preview(new ResizeCanvasItem(id, CanvasLayout.Snap(width), CanvasLayout.Snap(height))); }
        catch (CanvasValidationException exception) { PreviewValid = false; Error = exception.Message; Notify(); }
    }
    private void Preview(CanvasEdit edit)
    {
        previewEdit = edit; Error = null;
        try { DisplayItems = CanvasLayout.Apply(gestureStart!, edit, DateTimeOffset.UnixEpoch).Items; PreviewValid = true; }
        catch (CanvasValidationException exception)
        {
            PreviewValid = false; Error = exception.Message;
            // Invalid outlines are visual proposals only; no invalid snapshot is persisted.
            DisplayItems = gestureStart!.Items.Select(i => edit switch
            {
                MoveCanvasItems m when m.Ids.Contains(i.Id) => i with { Rect = i.Rect with { X = i.Rect.X + m.DeltaX, Y = i.Rect.Y + m.DeltaY } },
                ResizeCanvasItem r when r.Id == i.Id => i with { Rect = i.Rect with { Width = Math.Max(8, r.Width), Height = Math.Max(8, r.Height) } },
                _ => i
            }).ToArray();
        }
        Notify();
    }
    public async Task EndGestureAsync()
    {
        if (gestureStart is null) return;
        var edit = PreviewValid ? previewEdit : null; gestureStart = null; previewEdit = null; Guides = null; DisplayItems = Snapshot?.Items ?? [];
        if (edit is MoveCanvasItems { DeltaX: 0, DeltaY: 0 }) edit = null;
        if (edit is not null) await EditAsync(edit); else { PreviewValid = true; Notify(); }
    }
    public void CancelGesture()
    {
        gestureStart = null; previewEdit = null; Guides = null; PreviewValid = true; Error = null; DisplayItems = Snapshot?.Items ?? []; Notify();
    }
    public async Task EditAsync(CanvasEdit edit)
    {
        if (IsBusy || Reference is not { } reference || Snapshot is null) return;
        CancelGesture(); var request = revision; saving = true; Error = null; Notify();
        try
        {
            var updated = await service.EditAsync(reference, edit);
            if (request == revision && Reference == reference) { Snapshot = updated; DisplayItems = updated.Items; Selection.IntersectWith(updated.Items.Select(i => i.Id)); Widgets?.SetCanvas(updated); }
        }
        catch (Exception exception) { if (request == revision) Report(exception); }
        finally { saving = false; Notify(); }
    }
    public Task DeleteAsync() => Selection.Count == 0 ? Task.CompletedTask : EditAsync(new DeleteCanvasItems(Selection.ToArray()));
    private void Report(Exception exception)
    {
        if (exception is CanvasValidationException or CanvasOperationException or WorkspaceChangedException) Error = exception.Message;
        else { logger.LogError(exception, "Canvas presentation failed"); Error = "The layout could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(Reference), nameof(Snapshot), nameof(DisplayItems), nameof(Selection), nameof(IsBusy), nameof(HasPage), nameof(CanEdit), nameof(CanConfigure), nameof(Locked), nameof(Zoom), nameof(IsPreview), nameof(PreviewValid), nameof(Guides), nameof(Error), nameof(Status), nameof(Containers) }) OnPropertyChanged(name);
    }
}
