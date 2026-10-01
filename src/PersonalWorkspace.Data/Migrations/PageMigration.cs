namespace PersonalWorkspace.Data.Migrations;

internal static class PageMigration
{
    public static DatabaseMigration Definition { get; } = new(10, "Create page core", """
        CREATE TABLE Pages (
            ItemId TEXT NOT NULL PRIMARY KEY REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
            ParentPageId TEXT NULL REFERENCES Pages(ItemId) ON DELETE SET NULL,
            SortOrder INTEGER NOT NULL CHECK(SortOrder >= 0),
            Icon INTEGER NULL CHECK(Icon BETWEEN 1 AND 4),
            CHECK(ParentPageId IS NULL OR ParentPageId <> ItemId)
        );
        CREATE INDEX IX_Pages_ParentOrder ON Pages(ParentPageId, SortOrder, ItemId);
        CREATE TRIGGER TR_Pages_Identity BEFORE INSERT ON Pages
            WHEN NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.ItemId AND ItemType=5)
            BEGIN SELECT RAISE(ABORT,'Page requires a Page WorkspaceItem'); END;
        """);
}
