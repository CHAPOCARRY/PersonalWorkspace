using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteWidgetRepository
{
    public async Task<(WidgetPage Page, AssetAttachment Attachment)> ImportAsync(WorkspaceContext workspace, Guid page, Asset asset, AddWidget edit, DateTimeOffset now, CancellationToken token)
    {
        if (edit.Type is not (WidgetType.Image or WidgetType.Attachment) || edit.Source is not null)
            throw new WidgetValidationException("Choose an Image or Attachment Widget.");
        if (edit.Type == WidgetType.Image && asset.MediaType is not ("image/png" or "image/jpeg"))
            throw new WidgetValidationException("Choose a PNG or JPEG image.");
        await using var c = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var tx = c.BeginTransaction(deferred: false);
        var before = await Read(c, tx, page, null, token);
        var after = WidgetRules.Apply(new(before), edit, now);
        var attachment = await SqliteContentRepository.CompleteImport(c, tx, page, asset, token);
        after = await PrepareContent(c, tx, page, before, after, attachment, token);
        await Write(c, tx, page, before, after, token);
        await tx.CommitAsync(token); return (after, attachment);
    }
    private static async Task<WidgetPage> PrepareContent(SqliteConnection c, SqliteTransaction tx, Guid page, WidgetPage before, WidgetPage after, AssetAttachment? imported, CancellationToken token)
    {
        var result = new List<WidgetInstance>();
        foreach (var widget in after.Widgets)
        {
            var next = widget;
            if (!before.Widgets.Any(w => w.Id == widget.Id) && WidgetRules.IsContent(widget.Type))
            {
                if (widget.Type == WidgetType.RichText)
                {
                    var content = ""; var format = "markdown-subset"; var version = 1;
                    if (widget.RichText is { } original)
                    {
                        using var cmd = Command(c, tx, "SELECT DocumentId FROM RichTextReferences WHERE Id=$id AND OwnerItemId=$page", page);
                        Add(cmd, "$id", original.ReferenceId);
                        var value = await cmd.ExecuteScalarAsync(token) as string ?? throw new WidgetValidationException("Rich text document unavailable.");
                        var document = await SqliteContentRepository.DocumentById(c, tx, Guid.Parse(value), token) ?? throw new WidgetValidationException("Rich text document unavailable.");
                        content = document.Content; format = document.Format; version = document.FormatVersion;
                    }
                    var created = await SqliteContentRepository.CreateDocument(c, tx, page, new(Guid.NewGuid(), content, format, version, widget.CreatedAtUtc, widget.CreatedAtUtc), token);
                    next = widget with { RichText = new(created.Reference.Id) };
                }
                else
                {
                    Guid reference;
                    if (imported is not null) reference = imported.Reference.Id;
                    else if (widget.Asset is { } original)
                    {
                        using var cmd = Command(c, tx, "SELECT AssetId FROM AssetReferences WHERE Id=$id AND OwnerItemId=$page", page);
                        Add(cmd, "$id", original.ReferenceId);
                        var value = await cmd.ExecuteScalarAsync(token) as string ?? throw new WidgetValidationException("Attachment reference unavailable.");
                        reference = (await SqliteContentRepository.AssetLink(c, tx, page, Guid.Parse(value), widget.CreatedAtUtc, token)).Id;
                    }
                    else throw new WidgetValidationException("Choose a file to create this Widget.");
                    next = widget with { Asset = new(reference, widget.Asset?.Fit ?? ImageFitMode.Fit, widget.Asset?.AltText ?? "") };
                }
            }
            result.Add(next);
        }
        return after with { Widgets = result };
    }
    private static async Task WriteContent(SqliteConnection c, SqliteTransaction tx, Guid page, WidgetInstance widget, bool existing, CancellationToken token)
    {
        if (widget.RichText is { } rich && !existing)
        {
            using var cmd = Command(c, tx, "INSERT INTO WidgetRichTextSettings VALUES($id,$ref)", page);
            Add(cmd, "$id", widget.Id); Add(cmd, "$ref", rich.ReferenceId); await cmd.ExecuteNonQueryAsync(token);
        }
        if (widget.Asset is { } asset)
        {
            using var cmd = Command(c, tx, existing ? "UPDATE WidgetAssetSettings SET FitMode=$fit,AltText=$alt WHERE WidgetId=$id" : "INSERT INTO WidgetAssetSettings VALUES($id,$ref,$fit,$alt)", page);
            Add(cmd, "$id", widget.Id); Add(cmd, "$ref", asset.ReferenceId); Add(cmd, "$fit", (int)asset.Fit); Add(cmd, "$alt", asset.AltText); await cmd.ExecuteNonQueryAsync(token);
        }
    }
}
