using System.Text;

namespace PersonalWorkspace.Data.Migrations;

internal static class SearchMigration
{
    public static DatabaseMigration Definition { get; } = new(20, "Create Profile search index", Build());
    private static string Build()
    {
        var sql = new StringBuilder("""
            CREATE TABLE SearchIndexState(Id INTEGER PRIMARY KEY CHECK(Id=1), Version INTEGER NOT NULL, Rebuilding INTEGER NOT NULL CHECK(Rebuilding IN(0,1)), Generation INTEGER NOT NULL);
            INSERT INTO SearchIndexState VALUES(1,0,1,0);
            CREATE TABLE SearchDirtyDocuments(Kind INTEGER NOT NULL, SourceId TEXT NOT NULL, PRIMARY KEY(Kind,SourceId));
            CREATE TABLE SearchDocuments(
                Id INTEGER PRIMARY KEY, Kind INTEGER NOT NULL, SourceId TEXT NOT NULL, OwnerId TEXT NULL,
                Title TEXT NOT NULL, Metadata TEXT NOT NULL, Body TEXT NOT NULL, TitleKey TEXT NOT NULL, EntryDate TEXT NULL,
                UNIQUE(Kind,SourceId));
            CREATE INDEX IX_SearchDocuments_Owner ON SearchDocuments(OwnerId);
            CREATE VIRTUAL TABLE SearchDocumentsFts USING fts5(Title, Metadata, Body, content='SearchDocuments', content_rowid='Id', tokenize='unicode61 remove_diacritics 2', prefix='2 3 4');
            CREATE TRIGGER TR_SearchDocuments_Insert AFTER INSERT ON SearchDocuments BEGIN
                INSERT INTO SearchDocumentsFts(rowid,Title,Metadata,Body) VALUES(NEW.Id,NEW.Title,NEW.Metadata,NEW.Body); END;
            CREATE TRIGGER TR_SearchDocuments_Delete AFTER DELETE ON SearchDocuments BEGIN
                INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rowid,Title,Metadata,Body) VALUES('delete',OLD.Id,OLD.Title,OLD.Metadata,OLD.Body); END;
            CREATE TRIGGER TR_SearchDocuments_Update AFTER UPDATE ON SearchDocuments BEGIN
                INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rowid,Title,Metadata,Body) VALUES('delete',OLD.Id,OLD.Title,OLD.Metadata,OLD.Body);
                INSERT INTO SearchDocumentsFts(rowid,Title,Metadata,Body) VALUES(NEW.Id,NEW.Title,NEW.Metadata,NEW.Body); END;
            CREATE VIEW SearchSources AS
            SELECT 1 Kind,w.Id SourceId,w.Id OwnerId,w.Title,
                CASE WHEN w.ItemType=1 THEN coalesce(tv.Unit,'')||' '||coalesce(tv.CurrencyCode,'')||' '||
                    coalesce((SELECT group_concat(Name,' ') FROM (SELECT t.Name FROM ItemTags it JOIN Tags t ON t.Id=it.TagId WHERE it.ItemId=w.Id ORDER BY t.Id)),'')||' '||
                    coalesce((SELECT group_concat(Name,' ') FROM (SELECT s.Name FROM ItemSpaces isp JOIN Spaces s ON s.Id=isp.SpaceId WHERE isp.ItemId=w.Id ORDER BY s.Id)),'')
                WHEN w.ItemType=3 THEN coalesce(tr.Unit,'')||' '||coalesce(tr.CurrencyCode,'')
                WHEN w.ItemType=4 THEN coalesce((SELECT group_concat(Name,' ') FROM (SELECT f.Name FROM JournalFields f WHERE f.JournalId=w.Id ORDER BY f.SortOrder,f.Id)),'')||' '||
                    coalesce((SELECT group_concat(Label,' ') FROM (SELECT o.Label FROM JournalFieldOptions o JOIN JournalFields f ON f.Id=o.FieldId WHERE f.JournalId=w.Id ORDER BY f.SortOrder,f.Id,o.SortOrder,o.Id)),'')
                ELSE '' END Metadata,
                coalesce(t.Description,j.Description,l.Description,'') Body,NULL EntryDate,NULL Format,0 FormatVersion
            FROM WorkspaceItems w LEFT JOIN Tasks t ON t.ItemId=w.Id LEFT JOIN TaskValues tv ON tv.ItemId=w.Id
                LEFT JOIN Trackers tr ON tr.ItemId=w.Id LEFT JOIN Journals j ON j.ItemId=w.Id LEFT JOIN Lists l ON l.ItemId=w.Id
            UNION ALL
            SELECT 2,Id,ListId,Title,'',coalesce(Note,'')||' '||coalesce(Link,''),NULL,NULL,0 FROM ListItems
            UNION ALL
            SELECT 3,e.Id,e.JournalId,'','',coalesce((SELECT group_concat(Content,char(10)) FROM (
                SELECT f.Name||': '||coalesce(v.TextValue,(SELECT group_concat(Label,', ') FROM (
                    SELECT o.Label FROM JournalSelections s JOIN JournalFieldOptions o ON o.Id=s.OptionId WHERE s.EntryId=v.EntryId AND s.FieldId=v.FieldId ORDER BY o.SortOrder,o.Id))) Content
                FROM JournalValues v JOIN JournalFields f ON f.Id=v.FieldId WHERE v.EntryId=e.Id AND v.FieldType IN(0,1,8,9,11) ORDER BY f.SortOrder,f.Id)),''),e.EntryDate,NULL,0 FROM JournalEntries e
            UNION ALL
            SELECT 4,w.Id,c.PageId,coalesce(a.OriginalFileName,''),'',coalesce(d.Content,''),NULL,d.Format,coalesce(d.FormatVersion,0)
                FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId
                LEFT JOIN WidgetRichTextSettings rs ON rs.WidgetId=w.Id LEFT JOIN RichTextReferences r ON r.Id=rs.ReferenceId LEFT JOIN RichTextDocuments d ON d.Id=r.DocumentId
                LEFT JOIN WidgetAssetSettings ast ON ast.WidgetId=w.Id LEFT JOIN AssetReferences ar ON ar.Id=ast.ReferenceId LEFT JOIN Assets a ON a.Id=ar.AssetId
                WHERE (w.WidgetType=7 AND d.Id IS NOT NULL) OR (w.WidgetType IN(8,9) AND a.Id IS NOT NULL AND a.PendingDelete=0)
            UNION ALL
            SELECT 5,Id,NULL,Name,CASE TemplateType WHEN 1 THEN 'Task' WHEN 2 THEN 'List' WHEN 3 THEN 'Journal' WHEN 4 THEN 'Page' END,coalesce(Description,''),NULL,NULL,0 FROM Templates;
            """);
        static string Key(int kind, string id) => $"SELECT {kind},{id}";
        void Track(string table, string select, string? columns = null)
        {
            foreach (var (suffix, timing, op, row) in new[] { ("I", "AFTER", "INSERT", "NEW"), ("D", "BEFORE", "DELETE", "OLD"), ("UO", "BEFORE", "UPDATE", "OLD"), ("UN", "AFTER", "UPDATE", "NEW") })
            {
                var operation = op == "UPDATE" && columns is not null ? "UPDATE OF " + columns : op;
                // Explicit membership avoids duplicate inserts even when the outer canonical UPSERT overrides
                // the conflict policy of statements executed by its triggers.
                sql.AppendLine($"CREATE TRIGGER TR_Search_{table}_{suffix} {timing} {operation} ON {table} BEGIN INSERT INTO SearchDirtyDocuments(Kind,SourceId) WITH changes(Kind,SourceId) AS ({select.Replace("@", row)}) SELECT DISTINCT Kind,SourceId FROM changes ch WHERE NOT EXISTS(SELECT 1 FROM SearchDirtyDocuments d WHERE d.Kind=ch.Kind AND d.SourceId=ch.SourceId); UPDATE SearchIndexState SET Generation=Generation+1 WHERE Id=1; END;");
            }
        }
        Track("WorkspaceItems", Key(1,"@.Id"), "Title,ArchivedAtUtc,DeletedAtUtc");
        Track("Tasks", Key(1,"@.ItemId"), "Description");
        Track("TaskValues", Key(1,"@.ItemId"), "Unit,CurrencyCode");
        Track("Trackers", Key(1,"@.ItemId"), "Unit,CurrencyCode");
        Track("Journals", Key(1,"@.ItemId"), "Description");
        Track("Lists", Key(1,"@.ItemId"), "Description");
        Track("ListItems", Key(2,"@.Id"), "Title,Note,Link,ListId");
        Track("Templates", Key(5,"@.Id"), "Name,Description,TemplateType");
        foreach (var (table, column) in new[] { ("ItemTags","TagId"), ("ItemSpaces","SpaceId") }) Track(table, Key(1,"@.ItemId"));
        Track("Tags", "SELECT 1,ItemId FROM ItemTags WHERE TagId=@.Id", "Name");
        Track("Spaces", "SELECT 1,ItemId FROM ItemSpaces WHERE SpaceId=@.Id", "Name");
        Track("JournalEntries", Key(3,"@.Id"));
        Track("JournalValues", Key(3,"@.EntryId"));
        Track("JournalSelections", Key(3,"@.EntryId"));
        Track("JournalFields", "SELECT 1,@.JournalId UNION SELECT 3,EntryId FROM JournalValues WHERE FieldId=@.Id", "Name,FieldType,JournalId");
        Track("JournalFieldOptions", "SELECT 1,JournalId FROM JournalFields WHERE Id=@.FieldId UNION SELECT 3,EntryId FROM JournalSelections WHERE OptionId=@.Id", "Label,FieldId");
        Track("WidgetInstances", Key(4,"@.Id"), "CanvasItemId,WidgetType");
        Track("PageCanvasItems", "SELECT 4,Id FROM WidgetInstances WHERE CanvasItemId=@.Id", "PageId");
        Track("WidgetRichTextSettings", Key(4,"@.WidgetId"));
        Track("WidgetAssetSettings", Key(4,"@.WidgetId"), "ReferenceId");
        Track("RichTextReferences", "SELECT 4,WidgetId FROM WidgetRichTextSettings WHERE ReferenceId=@.Id");
        Track("AssetReferences", "SELECT 4,WidgetId FROM WidgetAssetSettings WHERE ReferenceId=@.Id");
        Track("RichTextDocuments", "SELECT 4,s.WidgetId FROM WidgetRichTextSettings s JOIN RichTextReferences r ON r.Id=s.ReferenceId WHERE r.DocumentId=@.Id", "Content,Format,FormatVersion");
        Track("Assets", "SELECT 4,s.WidgetId FROM WidgetAssetSettings s JOIN AssetReferences r ON r.Id=s.ReferenceId WHERE r.AssetId=@.Id", "OriginalFileName,PendingDelete");
        return sql.ToString();
    }
}
