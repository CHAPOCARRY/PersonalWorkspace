using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class TemplateService(ITemplateRepository repository, ITaskService tasks, IListService lists, IJournalService journals,
    ICurrentProfile current, IWorkspaceOperationGate gate, TimeProvider clock, ILogger<TemplateService> logger,
    IPageTemplateService? pages = null, IWorkspaceMaintenance? maintenance = null) : ITemplateService
{
    public Task<IReadOnlyList<TemplateMetadata>> CatalogAsync(Guid profile, TemplateType? type = null, CancellationToken token = default) => Run(profile, w =>
    { if (type is { } value) Type(value); return repository.CatalogAsync(w, type, token); }, token);
    public Task<IReadOnlyList<TemplateSource>> SourcesAsync(Guid profile, TemplateType type, CancellationToken token = default) => Run(profile, w =>
    { Type(type); return repository.SourcesAsync(w, type, token); }, token);
    public Task<TemplateSnapshot> ReadAsync(TemplateReference template, CancellationToken token = default) => Run(template.ProfileId, w => repository.ReadAsync(w, template.Id, token), token);
    public async Task<TemplateSnapshot> CaptureAsync(WorkspaceItemReference source, TemplateType type, string name, string? description = null, CancellationToken token = default)
    {
        Type(type); name = Name(name); description = Description(description);
        if (type == TemplateType.Page) return await Run(source.ProfileId, w =>
        {
            var now = clock.GetUtcNow();
            return PageTemplates.CaptureAsync(w, source.ItemId, new(Guid.NewGuid(), type, name, description, 1, now, now), true, token);
        }, token);
        var blueprint = await Capture(source, type, token);
        return await Run(source.ProfileId, async w =>
        {
            var now = clock.GetUtcNow(); var snapshot = new TemplateSnapshot(new(Guid.NewGuid(), type, name, description, 1, now, now), blueprint);
            await repository.SaveAsync(w with { Audit = (w.Audit ?? AuditOperation.User()) with { Source = new((AuditEntityType)(type == TemplateType.Task ? WorkspaceItemType.Task : type == TemplateType.List ? WorkspaceItemType.List : WorkspaceItemType.Journal), source.ItemId, blueprint.Title) } }, snapshot, true, token); return await repository.ReadAsync(w, snapshot.Metadata.Id, token);
        }, token);
    }
    public async Task<TemplateSnapshot> ReplaceAsync(TemplateReference template, WorkspaceItemReference source, CancellationToken token = default)
    {
        if (source.ProfileId != template.ProfileId) throw new WorkspaceChangedException();
        var old = await ReadAsync(template, token);
        if (old.Metadata.Type == TemplateType.Page) return await Run(template.ProfileId, async w =>
        {
            var latest = await repository.ReadAsync(w, template.Id, token);
            var result = await PageTemplates.CaptureAsync(w, source.ItemId, latest.Metadata with { UpdatedAtUtc = Next(latest.Metadata.UpdatedAtUtc) }, false, token);
            await Cleanup(w); return result;
        }, token);
        var blueprint = await Capture(source, old.Metadata.Type, token);
        return await Run(template.ProfileId, async w =>
        {
            // Recheck after the source read; concurrent metadata edits must not be overwritten.
            var latest = await repository.ReadAsync(w, template.Id, token);
            var snapshot = new TemplateSnapshot(latest.Metadata with { UpdatedAtUtc = Next(latest.Metadata.UpdatedAtUtc) }, blueprint);
            await repository.SaveAsync(w with { Audit = (w.Audit ?? AuditOperation.User()) with { Source = new((AuditEntityType)(old.Metadata.Type == TemplateType.Task ? WorkspaceItemType.Task : old.Metadata.Type == TemplateType.List ? WorkspaceItemType.List : WorkspaceItemType.Journal), source.ItemId, blueprint.Title) } }, snapshot, false, token); return await repository.ReadAsync(w, snapshot.Metadata.Id, token);
        }, token);
    }
    public Task RenameAsync(TemplateReference template, string name, string? description, CancellationToken token = default) => Run(template.ProfileId, async w =>
    {
        name = Name(name); description = Description(description); var old = (await repository.ReadAsync(w, template.Id, token)).Metadata;
        if (old.Name != name || old.Description != description) await repository.RenameAsync(w, template.Id, name, description, Next(old.UpdatedAtUtc), token);
        return true;
    }, token);
    public Task DeleteAsync(TemplateReference template, CancellationToken token = default) => Run(template.ProfileId, async w => { await repository.DeleteAsync(w, template.Id, token); await Cleanup(w); return true; }, token);
    private IPageTemplateService PageTemplates => pages ?? throw new TemplateValidationException("Page templates are unavailable.");
    public Task<WorkspaceItem> InstantiatePageAsync(TemplateReference template, string? title = null, Guid? parent = null, CancellationToken token = default) =>
        Run(template.ProfileId, w => PageTemplates.InstantiateAsync(w, template.Id, title, parent, token), token);
    private async Task Cleanup(WorkspaceContext w)
    {
        if (maintenance is null) return;
        try { await maintenance.RecoverAsync(w.ProfileId, CancellationToken.None); }
        catch (Exception e) { logger.LogWarning(e, "Template saved; content cleanup remains queued in Profile {ProfileId}", w.ProfileId); }
    }
    public async Task<WorkspaceItem> InstantiateAsync(TemplateReference template, string? title = null, CancellationToken token = default)
    {
        var snapshot = await ReadAsync(template, token);
        if (snapshot.Metadata.Type == TemplateType.Page) return await InstantiatePageAsync(template, title, null, token);
        title = string.IsNullOrWhiteSpace(title) ? snapshot.Blueprint.Title : title.Trim();
        var audit = AuditOperation.User(AuditAction.TemplateInstantiated, source: new(AuditEntityType.Template, template.Id, snapshot.Metadata.Name));
        // Release the read lease before entering a canonical service. Each service rechecks the originating Profile
        // and atomically writes the complete new entity. The captured blueprint never depends on its source.
        return snapshot.Blueprint switch
        {
            TaskBlueprint task => (await tasks.CreateAsync(template.ProfileId, task.Definition with { Title = title }, token, audit)).Item,
            ListBlueprint list => (await lists.CreateDefinitionAsync(template.ProfileId, list.Definition with { Title = title }, list.Items, token, audit)).Item,
            JournalBlueprint journal => (await journals.CreateDefinitionAsync(template.ProfileId, title, journal.Description, journal.Fields, token, audit)).Item,
            _ => throw new TemplateValidationException("This template type is not supported.")
        };
    }
    private async Task<TemplateBlueprint> Capture(WorkspaceItemReference source, TemplateType type, CancellationToken token)
    {
        switch (type)
        {
            case TemplateType.Task: return new TaskBlueprint(await tasks.CaptureDefinitionAsync(new(source.ProfileId, source.ItemId), token));
            case TemplateType.List:
                var list = await lists.ReadAsync(source, token);
                if (list.Definition.Item.DeletedAtUtc is not null) throw new TemplateValidationException("Restore this List from Trash before capturing its definition.");
                var d = list.Definition;
                return new ListBlueprint(ListRules.Validate(new ListDraft(d.Item.Title, d.Description, d.ShowQuantity, d.ShowPrices, d.CurrencyCode)),
                    list.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).Select(i => ListRules.Validate(new ListItemDraft(i.Title, i.Quantity, i.UnitPrice, i.Note, i.Link))).ToArray());
            case TemplateType.Journal:
                var journal = await journals.ReadDefinitionAsync(source, token);
                if (journal.Item.DeletedAtUtc is not null) throw new TemplateValidationException("Restore this Journal from Trash before capturing its definition.");
                return new JournalBlueprint(journal.Item.Title, journal.Description, journal.Fields.OrderBy(f => f.SortOrder).ThenBy(f => f.Id).Select(f =>
                    JournalRules.Validate(new(f.Name, f.Type, f.CurrencyCode, f.ScaleMin, f.ScaleMax, f.OrderedOptions.Select(o => new JournalOption(Guid.NewGuid(), o.Label, o.SortOrder)).ToArray()))).ToArray());
            default: throw new TemplateValidationException("Choose a supported template type.");
        }
    }
    private static void Type(TemplateType type) { if (!Enum.IsDefined(type)) throw new TemplateValidationException("Choose Task, List, Journal or Page."); }
    private static string Name(string name) => name.Trim() is { Length: > 0 and <= 200 } value && !value.Any(char.IsControl) ? value : throw new TemplateValidationException("Enter a template name of 1–200 characters without line breaks.");
    private static string? Description(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= 50000 ? text.Trim() : throw new TemplateValidationException("Keep the template description within 50,000 characters.");
    private DateTimeOffset Next(DateTimeOffset previous) => clock.GetUtcNow() > previous ? clock.GetUtcNow() : previous.AddTicks(1);
    private async Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } path) throw new WorkspaceChangedException();
        try { return await action(new WorkspaceContext(profile, path) { Audit = AuditOperation.User() }); }
        catch (TemplateValidationException) { throw; }
        catch (Exception e) when (e is PageValidationException or CanvasValidationException or WidgetValidationException or DecorationValidationException or ContentValidationException) { throw new TemplateValidationException(e.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Template operation failed in Profile {ProfileId}", profile); throw new TemplateOperationException("The template could not be loaded or saved. Check access to the workspace and try again.", e); }
    }
}
