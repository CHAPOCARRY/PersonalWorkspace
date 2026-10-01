namespace PersonalWorkspace.Data.Migrations;

internal static class CanvasMigration
{
    public static DatabaseMigration Definition { get; } = new(11, "Create canvas layout", """
        CREATE TABLE PageCanvasSettings (
            PageId TEXT NOT NULL PRIMARY KEY REFERENCES Pages(ItemId) ON DELETE CASCADE,
            LayoutLocked INTEGER NOT NULL CHECK(LayoutLocked IN (0,1)),
            ZoomPercent INTEGER NOT NULL CHECK(ZoomPercent BETWEEN 25 AND 200)
        );
        CREATE TABLE PageCanvasItems (
            Id TEXT NOT NULL PRIMARY KEY,
            PageId TEXT NOT NULL REFERENCES Pages(ItemId) ON DELETE CASCADE,
            ParentContainerId TEXT NULL,
            Kind INTEGER NOT NULL CHECK(Kind IN (0,1)),
            X INTEGER NOT NULL CHECK(typeof(X)='integer' AND X>=0 AND X%8=0),
            Y INTEGER NOT NULL CHECK(typeof(Y)='integer' AND Y>=0 AND Y%8=0),
            Width INTEGER NOT NULL CHECK(typeof(Width)='integer' AND Width>=64 AND Width%8=0),
            Height INTEGER NOT NULL CHECK(typeof(Height)='integer' AND Height>=48 AND Height%8=0),
            Title TEXT NOT NULL DEFAULT '' CHECK(length(Title)<=120),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            UNIQUE(Id,PageId),
            FOREIGN KEY(ParentContainerId,PageId) REFERENCES PageCanvasItems(Id,PageId) ON DELETE NO ACTION,
            CHECK(X+Width<=65536 AND Y+Height<=65536),
            CHECK(ParentContainerId IS NULL OR (Kind=0 AND ParentContainerId<>Id)),
            CHECK(Kind=1 OR Title='')
        );
        CREATE INDEX IX_CanvasItems_PageParent ON PageCanvasItems(PageId,ParentContainerId);
        CREATE TRIGGER TR_CanvasParent_Insert BEFORE INSERT ON PageCanvasItems
            WHEN NEW.ParentContainerId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PageCanvasItems p WHERE p.Id=NEW.ParentContainerId AND p.PageId=NEW.PageId AND p.Kind=1 AND p.ParentContainerId IS NULL)
            BEGIN SELECT RAISE(ABORT,'Parent must be a Container on the same Page'); END;
        CREATE TRIGGER TR_CanvasParent_Update BEFORE UPDATE OF ParentContainerId,PageId ON PageCanvasItems
            WHEN NEW.ParentContainerId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PageCanvasItems p WHERE p.Id=NEW.ParentContainerId AND p.PageId=NEW.PageId AND p.Kind=1 AND p.ParentContainerId IS NULL)
            BEGIN SELECT RAISE(ABORT,'Parent must be a Container on the same Page'); END;
        """);
}
