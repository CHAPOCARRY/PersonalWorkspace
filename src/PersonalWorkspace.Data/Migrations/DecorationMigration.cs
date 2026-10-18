namespace PersonalWorkspace.Data.Migrations;

internal static class DecorationMigration
{
    public static DatabaseMigration Definition { get; } = new(17, "Create canvas decorations", """
        CREATE TABLE CanvasDecorations (
            Id TEXT NOT NULL PRIMARY KEY,
            PageId TEXT NOT NULL REFERENCES Pages(ItemId) ON DELETE CASCADE,
            DecorationType INTEGER NOT NULL CHECK(DecorationType IN(0,1,2,3,4,5,6)),
            ZOrder INTEGER NOT NULL CHECK(typeof(ZOrder)='integer' AND ZOrder>=0),
            Color INTEGER NOT NULL CHECK(Color IN(0,1,2,3,4)),
            StrokeWidth INTEGER NOT NULL CHECK(typeof(StrokeWidth)='integer' AND StrokeWidth BETWEEN 1 AND 16),
            Filled INTEGER NOT NULL CHECK(Filled IN(0,1)),
            Dashed INTEGER NOT NULL CHECK(Dashed IN(0,1)),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            CHECK(DecorationType<3 OR Filled=0)
        );
        CREATE INDEX IX_Decorations_PageOrder ON CanvasDecorations(PageId,ZOrder,Id);
        CREATE TRIGGER TR_Decoration_Identity BEFORE UPDATE OF Id,PageId,DecorationType,CreatedAtUtc ON CanvasDecorations
            WHEN NEW.Id<>OLD.Id OR NEW.PageId<>OLD.PageId OR NEW.DecorationType<>OLD.DecorationType OR NEW.CreatedAtUtc<>OLD.CreatedAtUtc
            BEGIN SELECT RAISE(ABORT,'Decoration identity is immutable'); END;
        CREATE TABLE CanvasShapeSettings (
            DecorationId TEXT NOT NULL PRIMARY KEY REFERENCES CanvasDecorations(Id) ON DELETE CASCADE,
            X INTEGER NOT NULL CHECK(typeof(X)='integer' AND X>=0 AND X%8=0),
            Y INTEGER NOT NULL CHECK(typeof(Y)='integer' AND Y>=0 AND Y%8=0),
            Width INTEGER NOT NULL CHECK(typeof(Width)='integer' AND Width>=16 AND Width%8=0),
            Height INTEGER NOT NULL CHECK(typeof(Height)='integer' AND Height>=16 AND Height%8=0),
            CHECK(X+Width<=65536 AND Y+Height<=65536)
        );
        CREATE TRIGGER TR_DecorationShape_Type BEFORE INSERT ON CanvasShapeSettings
            WHEN NOT EXISTS(SELECT 1 FROM CanvasDecorations WHERE Id=NEW.DecorationId AND DecorationType BETWEEN 0 AND 2)
            BEGIN SELECT RAISE(ABORT,'Shape geometry requires a shape'); END;
        CREATE TRIGGER TR_DecorationShape_Identity BEFORE UPDATE OF DecorationId ON CanvasShapeSettings
            BEGIN SELECT RAISE(ABORT,'Shape ownership is immutable'); END;
        CREATE TABLE CanvasDecorationEndpoints (
            DecorationId TEXT NOT NULL REFERENCES CanvasDecorations(Id) ON DELETE CASCADE,
            Endpoint INTEGER NOT NULL CHECK(Endpoint IN(0,1)),
            X REAL NOT NULL CHECK(X>=0 AND X<=65536),
            Y REAL NOT NULL CHECK(Y>=0 AND Y<=65536),
            TargetCanvasItemId TEXT NULL REFERENCES PageCanvasItems(Id) ON DELETE SET NULL,
            Anchor INTEGER NOT NULL CHECK(Anchor IN(0,1,2,3,4)),
            PRIMARY KEY(DecorationId,Endpoint)
        );
        CREATE INDEX IX_DecorationEndpoint_Target ON CanvasDecorationEndpoints(TargetCanvasItemId) WHERE TargetCanvasItemId IS NOT NULL;
        CREATE TRIGGER TR_DecorationEndpoint_Insert BEFORE INSERT ON CanvasDecorationEndpoints
            WHEN NOT EXISTS(SELECT 1 FROM CanvasDecorations d WHERE d.Id=NEW.DecorationId AND d.DecorationType BETWEEN 3 AND 5
                AND (NEW.TargetCanvasItemId IS NULL OR (d.DecorationType=5 AND EXISTS(SELECT 1 FROM PageCanvasItems c WHERE c.Id=NEW.TargetCanvasItemId AND c.PageId=d.PageId))))
            BEGIN SELECT RAISE(ABORT,'Connector target must belong to the same Page'); END;
        CREATE TRIGGER TR_DecorationEndpoint_Update BEFORE UPDATE ON CanvasDecorationEndpoints
            WHEN NEW.DecorationId<>OLD.DecorationId OR NEW.Endpoint<>OLD.Endpoint OR NOT EXISTS(SELECT 1 FROM CanvasDecorations d WHERE d.Id=NEW.DecorationId AND d.DecorationType BETWEEN 3 AND 5
                AND (NEW.TargetCanvasItemId IS NULL OR (d.DecorationType=5 AND EXISTS(SELECT 1 FROM PageCanvasItems c WHERE c.Id=NEW.TargetCanvasItemId AND c.PageId=d.PageId))))
            BEGIN SELECT RAISE(ABORT,'Connector target must belong to the same Page'); END;
        -- All layout deletion paths (including Widget removal) preserve attached endpoints.
        -- Container children are reparented before Container deletion by the existing repository.
        CREATE TRIGGER TR_DecorationTarget_Delete BEFORE DELETE ON PageCanvasItems
            BEGIN
                UPDATE CanvasDecorationEndpoints SET
                    X=OLD.X+COALESCE((SELECT X+8 FROM PageCanvasItems WHERE Id=OLD.ParentContainerId),0)
                        +CASE Anchor WHEN 0 THEN 0 WHEN 1 THEN OLD.Width ELSE OLD.Width/2.0 END,
                    Y=OLD.Y+COALESCE((SELECT Y+40 FROM PageCanvasItems WHERE Id=OLD.ParentContainerId),0)
                        +CASE Anchor WHEN 2 THEN 0 WHEN 3 THEN OLD.Height ELSE OLD.Height/2.0 END,
                    TargetCanvasItemId=NULL
                WHERE TargetCanvasItemId=OLD.Id;
            END;
        CREATE TABLE CanvasStrokePoints (
            DecorationId TEXT NOT NULL REFERENCES CanvasDecorations(Id) ON DELETE CASCADE,
            PointIndex INTEGER NOT NULL CHECK(typeof(PointIndex)='integer' AND PointIndex BETWEEN 0 AND 8191),
            X REAL NOT NULL CHECK(X>=0 AND X<=65536),
            Y REAL NOT NULL CHECK(Y>=0 AND Y<=65536),
            PRIMARY KEY(DecorationId,PointIndex)
        );
        CREATE TRIGGER TR_DecorationPoint_Type BEFORE INSERT ON CanvasStrokePoints
            WHEN NOT EXISTS(SELECT 1 FROM CanvasDecorations WHERE Id=NEW.DecorationId AND DecorationType=6)
            BEGIN SELECT RAISE(ABORT,'Drawing points require a stroke'); END;
        CREATE TRIGGER TR_DecorationPoint_Identity BEFORE UPDATE OF DecorationId,PointIndex ON CanvasStrokePoints
            BEGIN SELECT RAISE(ABORT,'Stroke point ownership is immutable'); END;
        """);
}
