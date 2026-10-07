namespace PersonalWorkspace.Data.Migrations;

internal static class WidgetMigration
{
    public static DatabaseMigration Definition { get; } = new(12, "Create page widgets", """
        CREATE TABLE WidgetInstances (
            Id TEXT NOT NULL PRIMARY KEY,
            CanvasItemId TEXT NOT NULL UNIQUE REFERENCES PageCanvasItems(Id) ON DELETE CASCADE,
            WidgetType INTEGER NOT NULL CHECK(WidgetType BETWEEN 0 AND 5),
            SourceItemId TEXT NULL REFERENCES WorkspaceItems(Id) ON DELETE SET NULL,
            PresentationMode INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            CHECK((WidgetType=0 AND PresentationMode IN (0,1,2)) OR
                  (WidgetType=1 AND PresentationMode IN (2,3,4)) OR
                  (WidgetType=2 AND PresentationMode=5) OR
                  (WidgetType IN (3,5) AND PresentationMode=0) OR
                  (WidgetType=4 AND PresentationMode=6))
        );
        CREATE INDEX IX_WidgetInstances_Source ON WidgetInstances(SourceItemId);
        CREATE TABLE WidgetChartSettings (
            WidgetId TEXT NOT NULL PRIMARY KEY REFERENCES WidgetInstances(Id) ON DELETE CASCADE,
            ChartKind INTEGER NOT NULL CHECK(ChartKind BETWEEN 0 AND 2),
            RangePreset INTEGER NOT NULL CHECK(RangePreset BETWEEN 0 AND 3)
        );
        CREATE TRIGGER TR_Widget_Host_Insert BEFORE INSERT ON WidgetInstances
            WHEN NOT EXISTS(SELECT 1 FROM PageCanvasItems WHERE Id=NEW.CanvasItemId AND Kind=0)
            BEGIN SELECT RAISE(ABORT,'Widget requires a Block'); END;
        CREATE TRIGGER TR_Widget_Identity_Update BEFORE UPDATE OF Id,CanvasItemId,WidgetType,CreatedAtUtc ON WidgetInstances
            WHEN NEW.Id<>OLD.Id OR NEW.CanvasItemId<>OLD.CanvasItemId OR NEW.WidgetType<>OLD.WidgetType OR NEW.CreatedAtUtc<>OLD.CreatedAtUtc
            BEGIN SELECT RAISE(ABORT,'Widget identity is immutable'); END;
        CREATE TRIGGER TR_Widget_Source_Insert BEFORE INSERT ON WidgetInstances
            WHEN NEW.SourceItemId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM WorkspaceItems w JOIN PageCanvasItems c ON c.Id=NEW.CanvasItemId
                WHERE w.Id=NEW.SourceItemId AND w.ItemType=CASE NEW.WidgetType WHEN 0 THEN 1 WHEN 1 THEN 3 WHEN 2 THEN 3 WHEN 3 THEN 2 WHEN 4 THEN 4 WHEN 5 THEN 5 END
                AND (NEW.WidgetType<>5 OR w.Id<>c.PageId)
                AND (NEW.WidgetType<>2 OR EXISTS(SELECT 1 FROM Trackers WHERE ItemId=w.Id AND ValueType<>6)))
            BEGIN SELECT RAISE(ABORT,'Widget source type or Page is invalid'); END;
        CREATE TRIGGER TR_Widget_Source_Update BEFORE UPDATE OF SourceItemId ON WidgetInstances
            WHEN NEW.SourceItemId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM WorkspaceItems w JOIN PageCanvasItems c ON c.Id=NEW.CanvasItemId
                WHERE w.Id=NEW.SourceItemId AND w.ItemType=CASE NEW.WidgetType WHEN 0 THEN 1 WHEN 1 THEN 3 WHEN 2 THEN 3 WHEN 3 THEN 2 WHEN 4 THEN 4 WHEN 5 THEN 5 END
                AND (NEW.WidgetType<>5 OR w.Id<>c.PageId)
                AND (NEW.WidgetType<>2 OR EXISTS(SELECT 1 FROM Trackers WHERE ItemId=w.Id AND ValueType<>6)))
            BEGIN SELECT RAISE(ABORT,'Widget source type or Page is invalid'); END;
        CREATE TRIGGER TR_WidgetChart_Type BEFORE INSERT ON WidgetChartSettings
            WHEN NOT EXISTS(SELECT 1 FROM WidgetInstances WHERE Id=NEW.WidgetId AND WidgetType=2)
            BEGIN SELECT RAISE(ABORT,'Chart settings require a Tracker Chart Widget'); END;
        CREATE TRIGGER TR_WidgetHost_Kind BEFORE UPDATE OF Kind ON PageCanvasItems
            WHEN NEW.Kind<>0 AND EXISTS(SELECT 1 FROM WidgetInstances WHERE CanvasItemId=NEW.Id)
            BEGIN SELECT RAISE(ABORT,'A Widget host must remain a Block'); END;
        """);
}
