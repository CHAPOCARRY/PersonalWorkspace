using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public enum DecorationTool { Layout, Select, Rectangle, RoundedRectangle, Ellipse, Line, Arrow, Connector, Pen }
public sealed class DecorationWorkspaceViewModel : ObservableObject
{
    private readonly IDecorationService service;
    private readonly ICurrentProfile current;
    private readonly ILogger<DecorationWorkspaceViewModel> logger;
    private int revision;
    private bool loading, saving;
    private DecorationSnapshot? gestureStart;
    private DecorationEdit? previewEdit;
    public DecorationWorkspaceViewModel(IDecorationService service, ICurrentProfile current, ILogger<DecorationWorkspaceViewModel> logger)
    {
        this.service=service;this.current=current;this.logger=logger;
        current.Changed+=(_,_)=>{if(Reference?.ProfileId!=current.Current?.Id)Clear();};
    }
    public WorkspaceItemReference? Reference {get;private set;}
    public DecorationSnapshot? Snapshot {get;private set;}
    public IReadOnlyList<CanvasDecoration> DisplayItems {get;private set;}=[];
    public Guid? Selected {get;private set;}
    public DecorationTool Tool {get;private set;}
    public bool IsBusy=>loading||saving;
    public bool IsPreview=>gestureStart is not null;
    public bool PreviewValid {get;private set;}=true;
    public string? Error {get;private set;}
    public bool CanEdit=>!IsBusy && Snapshot?.Canvas is {PageActive:true,Settings.LayoutLocked:false};
    public async Task OpenAsync(WorkspaceItemReference? reference)
    { Clear(); if(reference is null || reference.ProfileId!=current.Current?.Id)return;Reference=reference;await RefreshAsync(); }
    public async Task RefreshAsync()
    {
        if(Reference is not {} reference)return;var request=revision;loading=true;Notify();
        try{var snapshot=await service.GetAsync(reference);if(request==revision){Snapshot=snapshot;DisplayItems=snapshot.Items;if(!snapshot.Items.Any(i=>i.Id==Selected))Selected=null;}}
        catch(Exception e){if(request==revision)Report(e);}
        finally{if(request==revision){loading=false;Notify();}}
    }
    public void SetCanvas(CanvasSnapshot canvas)
    {
        if(Snapshot is null || Snapshot.Canvas.PageId!=canvas.PageId)return;
        var previous=Snapshot.Canvas;
        DecorationEndpoint? DetachMissing(DecorationEndpoint? endpoint) => endpoint?.Target is {} id && !canvas.Items.Any(i=>i.Id==id)
            ? endpoint with {Point=DecorationRules.Resolve(endpoint,previous),Target=null} : endpoint;
        var items=Snapshot.Items.Select(item=>item with {Start=DetachMissing(item.Start),End=DetachMissing(item.End)}).ToArray();
        Snapshot=Snapshot with {Canvas=canvas,Items=items};
        if(!IsPreview)DisplayItems=items;
        if(canvas.Settings.LayoutLocked || !canvas.PageActive){Tool=DecorationTool.Layout;CancelGesture();}
    }
    public void Clear()
    {revision++;Reference=null;Snapshot=null;DisplayItems=[];Selected=null;Tool=DecorationTool.Layout;gestureStart=null;previewEdit=null;loading=false;Error=null;PreviewValid=true;Notify();}
    public void SetTool(DecorationTool tool)
    {
        if(!Enum.IsDefined(tool) || tool!=DecorationTool.Layout && !CanEdit)return;
        CancelGesture();Tool=tool;Selected=null;Notify();
    }
    public void Select(Guid? id)
    {if(IsBusy || IsPreview)return;Selected=DisplayItems.Any(i=>i.Id==id)?id:null;Error=null;Notify();}
    public bool BeginGesture()
    {if(!CanEdit || Snapshot is null)return false;gestureStart=Snapshot;previewEdit=null;Error=null;PreviewValid=true;return true;}
    public void Preview(DecorationEdit edit)
    {
        if(gestureStart is null)return;previewEdit=edit;
        try{DisplayItems=DecorationRules.Apply(gestureStart,edit,DateTimeOffset.UnixEpoch).Items;PreviewValid=true;Error=null;}
        catch(Exception e) when(e is DecorationValidationException or CanvasValidationException){PreviewValid=false;Error=e.Message;DisplayItems=gestureStart.Items;}
        Notify();
    }
    public void CancelGesture()
    {gestureStart=null;previewEdit=null;DisplayItems=Snapshot?.Items??[];PreviewValid=true;Error=null;Notify();}
    public async Task EndGestureAsync()
    {
        var edit=PreviewValid?previewEdit:null;var error=Error;CancelGesture();
        if(edit is not null)await EditAsync(edit);else{Error=error;Notify();}
    }
    public async Task EditAsync(DecorationEdit edit)
    {
        if(!CanEdit || Reference is not {} reference)return;
        CancelGesture();var request=revision;var previous=Snapshot!.Items.Select(i=>i.Id).ToHashSet();saving=true;Error=null;Notify();
        try
        {
            var result=await service.EditAsync(reference,edit);
            if(request==revision)
            {
                Snapshot=result;DisplayItems=result.Items;
                if(edit is CreateDecoration or DuplicateDecoration){Selected=result.Items.Single(i=>!previous.Contains(i.Id)).Id;Tool=DecorationTool.Select;}
                if(!result.Items.Any(i=>i.Id==Selected))Selected=null;
            }
        }
        catch(Exception e){if(request==revision)Report(e);}
        finally{saving=false;Notify();}
    }
    private void Report(Exception e)
    {
        if(e is DecorationValidationException or DecorationOperationException or CanvasValidationException or WorkspaceChangedException)Error=e.Message;
        else{logger.LogError(e,"Decoration presentation failed");Error="Decorations could not be updated. Please try again.";}
    }
    private void Notify(){foreach(var property in new[]{nameof(DisplayItems),nameof(IsBusy),nameof(Selected),nameof(Tool),nameof(CanEdit),nameof(Error)})OnPropertyChanged(property);}
}
