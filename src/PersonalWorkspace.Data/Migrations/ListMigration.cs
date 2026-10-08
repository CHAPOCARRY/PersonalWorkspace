namespace PersonalWorkspace.Data.Migrations;

internal static class ListMigration
{
    public static DatabaseMigration Definition { get; } = new(13, "Create lists core", """
        CREATE TABLE Lists (
            ItemId TEXT NOT NULL PRIMARY KEY REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
            Description TEXT NOT NULL,
            ShowQuantity INTEGER NOT NULL CHECK(ShowQuantity IN (0,1)),
            ShowPrices INTEGER NOT NULL CHECK(ShowPrices IN (0,1)),
            CurrencyCode TEXT NULL CHECK(CurrencyCode IS NULL OR (length(CurrencyCode)=3 AND CurrencyCode NOT GLOB '*[^A-Z]*'))
        );
        CREATE TRIGGER TR_Lists_Identity BEFORE INSERT ON Lists
            WHEN NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.ItemId AND ItemType=6)
            BEGIN SELECT RAISE(ABORT,'List requires a List WorkspaceItem'); END;
        CREATE TABLE ListItems (
            Id TEXT NOT NULL PRIMARY KEY,
            ListId TEXT NOT NULL REFERENCES Lists(ItemId) ON DELETE CASCADE,
            Title TEXT NOT NULL CHECK(length(trim(Title)) BETWEEN 1 AND 200),
            IsChecked INTEGER NOT NULL CHECK(IsChecked IN (0,1)),
            Quantity TEXT NULL,
            UnitPrice TEXT NULL,
            Note TEXT NULL,
            Link TEXT NULL,
            SortOrder INTEGER NOT NULL CHECK(SortOrder >= 0),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IX_ListItems_Order ON ListItems(ListId,SortOrder);
        CREATE INDEX IX_ListItems_CheckedOrder ON ListItems(ListId,IsChecked,SortOrder);
        """);
}
