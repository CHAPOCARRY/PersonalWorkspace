namespace PersonalWorkspace.Data.Migrations;

public static class PageTemplateMigration
{
    public static DatabaseMigration Definition { get; } = new(19, "Create page templates", CatalogUpgrade() + Schema);

    private static string CatalogUpgrade()
    {
        // Preserve ALL dependent rows before dropping anything. Drop children first: FK enforcement
        // stays on, and no live child remains for DROP TABLE's implicit DELETE to cascade into.
        string[] tables = ["Templates", "TaskTemplateDefinitions", "ListTemplateDefinitions", "ListTemplateItems",
            "JournalTemplateDefinitions", "JournalTemplateFields", "JournalTemplateOptions"];
        return string.Concat(tables.Select(t => $"CREATE TEMP TABLE P21_{t} AS SELECT * FROM {t};\n"))
            + string.Concat(tables.Reverse().Select(t => $"DROP TABLE {t};\n"))
            + TemplateMigration.Definition.Sql.Replace("CHECK(TemplateType IN (1,2,3))", "CHECK(TemplateType IN (1,2,3,4))", StringComparison.Ordinal)
            + "\n" + string.Concat(tables.Select(t => $"INSERT INTO {t} SELECT * FROM P21_{t}; DROP TABLE P21_{t};\n"));
    }

    private const string Schema = """
        CREATE TABLE PageTemplateDefinitions (
            TemplateId TEXT NOT NULL PRIMARY KEY,
            TemplateType INTEGER NOT NULL DEFAULT 4 CHECK(TemplateType=4),
            Title TEXT NOT NULL CHECK(length(trim(Title)) BETWEEN 1 AND 200),
            Icon INTEGER NOT NULL CHECK(Icon BETWEEN 0 AND 4),
            SourcePageId TEXT NOT NULL,
            LayoutLocked INTEGER NOT NULL CHECK(LayoutLocked IN (0,1)),
            ZoomPercent INTEGER NOT NULL CHECK(ZoomPercent BETWEEN 25 AND 200 AND ZoomPercent%25=0),
            FOREIGN KEY(TemplateId,TemplateType) REFERENCES Templates(Id,TemplateType) ON DELETE CASCADE
        );
        CREATE TABLE PageTemplateCanvasItems (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES PageTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            ParentContainerId TEXT NULL,
            Kind INTEGER NOT NULL CHECK(Kind IN (0,1)),
            X INTEGER NOT NULL CHECK(X>=0 AND X%8=0), Y INTEGER NOT NULL CHECK(Y>=0 AND Y%8=0),
            Width INTEGER NOT NULL CHECK(Width>=64 AND Width%8=0), Height INTEGER NOT NULL CHECK(Height>=48 AND Height%8=0),
            Title TEXT NOT NULL CHECK(length(Title)<=120),
            UNIQUE(Id,TemplateId),
            CHECK(X+Width<=65536 AND Y+Height<=65536),
            CHECK(Kind=0 OR ParentContainerId IS NULL), CHECK(Id<>ParentContainerId),
            FOREIGN KEY(ParentContainerId,TemplateId) REFERENCES PageTemplateCanvasItems(Id,TemplateId) DEFERRABLE INITIALLY DEFERRED
        );
        CREATE INDEX IX_PageTemplateCanvas_Owner ON PageTemplateCanvasItems(TemplateId);
        CREATE TRIGGER TR_PageTemplateCanvas_Parent BEFORE INSERT ON PageTemplateCanvasItems
            WHEN NEW.ParentContainerId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PageTemplateCanvasItems WHERE Id=NEW.ParentContainerId AND TemplateId=NEW.TemplateId AND Kind=1)
            BEGIN SELECT RAISE(ABORT,'Choose a Container in this template'); END;
        CREATE TABLE PageTemplateWidgets (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES PageTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            CanvasItemId TEXT NOT NULL UNIQUE,
            WidgetType INTEGER NOT NULL CHECK(WidgetType BETWEEN 0 AND 9),
            SourceItemId TEXT NULL REFERENCES WorkspaceItems(Id) ON DELETE SET NULL,
            PresentationMode INTEGER NOT NULL,
            UNIQUE(Id,TemplateId), UNIQUE(Id,WidgetType),
            FOREIGN KEY(CanvasItemId,TemplateId) REFERENCES PageTemplateCanvasItems(Id,TemplateId) ON DELETE CASCADE,
            CHECK((WidgetType=0 AND PresentationMode IN (0,1,2)) OR (WidgetType=1 AND PresentationMode IN (2,3,4)) OR
                (WidgetType=2 AND PresentationMode=5) OR (WidgetType IN (3,5,7,8,9) AND PresentationMode=0) OR
                (WidgetType=4 AND PresentationMode=6) OR (WidgetType=6 AND PresentationMode IN (7,8,9))),
            CHECK(WidgetType<7 OR SourceItemId IS NULL)
        );
        CREATE INDEX IX_PageTemplateWidgets_Source ON PageTemplateWidgets(SourceItemId);
        CREATE INDEX IX_PageTemplateWidgets_Owner ON PageTemplateWidgets(TemplateId);
        CREATE TRIGGER TR_PageTemplateWidget_Host BEFORE INSERT ON PageTemplateWidgets
            WHEN NOT EXISTS(SELECT 1 FROM PageTemplateCanvasItems WHERE Id=NEW.CanvasItemId AND TemplateId=NEW.TemplateId AND Kind=0)
            BEGIN SELECT RAISE(ABORT,'Widgets require a Block in this template'); END;
        CREATE TRIGGER TR_PageTemplateWidget_Source BEFORE INSERT ON PageTemplateWidgets
            WHEN NEW.SourceItemId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.SourceItemId AND ItemType=CASE NEW.WidgetType WHEN 0 THEN 1 WHEN 1 THEN 3 WHEN 2 THEN 3 WHEN 3 THEN 2 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 6 THEN 6 END)
            BEGIN SELECT RAISE(ABORT,'Choose a compatible canonical source'); END;
        CREATE TRIGGER TR_PageTemplateWidget_SourceUpdate BEFORE UPDATE OF SourceItemId ON PageTemplateWidgets
            WHEN NEW.SourceItemId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.SourceItemId AND ItemType=CASE NEW.WidgetType WHEN 0 THEN 1 WHEN 1 THEN 3 WHEN 2 THEN 3 WHEN 3 THEN 2 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 6 THEN 6 END)
            BEGIN SELECT RAISE(ABORT,'Choose a compatible canonical source'); END;
        CREATE TRIGGER TR_PageTemplateWidget_Identity BEFORE UPDATE OF Id,TemplateId,CanvasItemId,WidgetType ON PageTemplateWidgets
            BEGIN SELECT RAISE(ABORT,'Replace snapshot structure explicitly'); END;
        CREATE TABLE PageTemplateChartSettings (
            WidgetId TEXT NOT NULL PRIMARY KEY REFERENCES PageTemplateWidgets(Id) ON DELETE CASCADE,
            WidgetType INTEGER NOT NULL DEFAULT 2 CHECK(WidgetType=2),
            ChartKind INTEGER NOT NULL CHECK(ChartKind BETWEEN 0 AND 2), RangePreset INTEGER NOT NULL CHECK(RangePreset BETWEEN 0 AND 3),
            FOREIGN KEY(WidgetId,WidgetType) REFERENCES PageTemplateWidgets(Id,WidgetType) ON DELETE CASCADE
        );
        CREATE TABLE PageTemplateListSettings (
            WidgetId TEXT NOT NULL PRIMARY KEY REFERENCES PageTemplateWidgets(Id) ON DELETE CASCADE,
            WidgetType INTEGER NOT NULL DEFAULT 6 CHECK(WidgetType=6), ItemFilter INTEGER NOT NULL CHECK(ItemFilter BETWEEN 0 AND 2),
            FOREIGN KEY(WidgetId,WidgetType) REFERENCES PageTemplateWidgets(Id,WidgetType) ON DELETE CASCADE
        );
        CREATE TABLE PageTemplateRichTextContent (
            WidgetId TEXT NOT NULL PRIMARY KEY REFERENCES PageTemplateWidgets(Id) ON DELETE CASCADE,
            WidgetType INTEGER NOT NULL DEFAULT 7 CHECK(WidgetType=7),
            Content TEXT NOT NULL CHECK(length(Content)<=1000000), Format TEXT NOT NULL CHECK(Format='markdown-subset'),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            FOREIGN KEY(WidgetId,WidgetType) REFERENCES PageTemplateWidgets(Id,WidgetType) ON DELETE CASCADE
        );
        CREATE TABLE TemplateAssetReferences (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES PageTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            AssetId TEXT NOT NULL REFERENCES Assets(Id) ON DELETE RESTRICT,
            CreatedAtUtc TEXT NOT NULL, UNIQUE(TemplateId,AssetId), UNIQUE(Id,TemplateId)
        );
        CREATE INDEX IX_TemplateAssetReferences_Asset ON TemplateAssetReferences(AssetId);
        CREATE TRIGGER TR_TemplateAssetReference_Insert BEFORE INSERT ON TemplateAssetReferences
            WHEN NOT EXISTS(SELECT 1 FROM Assets WHERE Id=NEW.AssetId AND PendingDelete=0)
            BEGIN SELECT RAISE(ABORT,'Choose a live Asset; pending cleanup cannot be reversed'); END;
        CREATE TRIGGER TR_TemplateAssetReference_Immutable BEFORE UPDATE ON TemplateAssetReferences
            BEGIN SELECT RAISE(ABORT,'Remove and create retention explicitly'); END;
        CREATE TABLE PageTemplateAssetSettings (
            WidgetId TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL, ReferenceId TEXT NOT NULL,
            WidgetType INTEGER NOT NULL CHECK(WidgetType IN (8,9)),
            FitMode INTEGER NOT NULL CHECK(FitMode IN (0,1)), AltText TEXT NOT NULL CHECK(length(AltText)<=1000),
            FOREIGN KEY(WidgetId,TemplateId) REFERENCES PageTemplateWidgets(Id,TemplateId) ON DELETE CASCADE,
            FOREIGN KEY(WidgetId,WidgetType) REFERENCES PageTemplateWidgets(Id,WidgetType) ON DELETE CASCADE,
            FOREIGN KEY(ReferenceId,TemplateId) REFERENCES TemplateAssetReferences(Id,TemplateId) DEFERRABLE INITIALLY DEFERRED
        );
        CREATE TRIGGER TR_PageTemplateAsset_Image BEFORE INSERT ON PageTemplateAssetSettings
            WHEN NEW.WidgetType=8 AND NOT EXISTS(SELECT 1 FROM TemplateAssetReferences r JOIN Assets a ON a.Id=r.AssetId WHERE r.Id=NEW.ReferenceId AND a.PendingDelete=0 AND a.MediaType IN ('image/png','image/jpeg'))
            BEGIN SELECT RAISE(ABORT,'Images require a live PNG or JPEG'); END;
        DROP TRIGGER TR_Asset_Pending;
        DROP TRIGGER TR_AssetReference_Cleanup;
        CREATE TRIGGER TR_Asset_Pending BEFORE UPDATE OF PendingDelete ON Assets
            WHEN NEW.PendingDelete<>1 OR EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=OLD.Id) OR EXISTS(SELECT 1 FROM TemplateAssetReferences WHERE AssetId=OLD.Id)
            BEGIN SELECT RAISE(ABORT,'Only unowned assets may be marked for cleanup'); END;
        CREATE TRIGGER TR_AssetReference_Cleanup AFTER DELETE ON AssetReferences
            WHEN NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=OLD.AssetId) AND NOT EXISTS(SELECT 1 FROM TemplateAssetReferences WHERE AssetId=OLD.AssetId)
            BEGIN UPDATE Assets SET PendingDelete=1 WHERE Id=OLD.AssetId; END;
        CREATE TRIGGER TR_TemplateAssetReference_Cleanup AFTER DELETE ON TemplateAssetReferences
            WHEN NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=OLD.AssetId) AND NOT EXISTS(SELECT 1 FROM TemplateAssetReferences WHERE AssetId=OLD.AssetId)
            BEGIN UPDATE Assets SET PendingDelete=1 WHERE Id=OLD.AssetId; END;
        CREATE TABLE PageTemplateDecorations (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES PageTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 0 AND 6), ZOrder INTEGER NOT NULL CHECK(ZOrder>=0),
            TieOrder INTEGER NOT NULL CHECK(TieOrder>=0), Color INTEGER NOT NULL CHECK(Color BETWEEN 0 AND 4),
            StrokeWidth INTEGER NOT NULL CHECK(StrokeWidth BETWEEN 1 AND 16), Filled INTEGER NOT NULL CHECK(Filled IN (0,1)),
            Dashed INTEGER NOT NULL CHECK(Dashed IN (0,1)), UNIQUE(Id,TemplateId), UNIQUE(TemplateId,TieOrder), CHECK(Kind<=2 OR Filled=0)
        );
        CREATE TABLE PageTemplateShapeSettings (
            DecorationId TEXT NOT NULL PRIMARY KEY REFERENCES PageTemplateDecorations(Id) ON DELETE CASCADE,
            X INTEGER NOT NULL CHECK(X>=0 AND X%8=0), Y INTEGER NOT NULL CHECK(Y>=0 AND Y%8=0),
            Width INTEGER NOT NULL CHECK(Width>=16 AND Width%8=0), Height INTEGER NOT NULL CHECK(Height>=16 AND Height%8=0),
            CHECK(X+Width<=65536 AND Y+Height<=65536)
        );
        CREATE TABLE PageTemplateDecorationEndpoints (
            DecorationId TEXT NOT NULL, TemplateId TEXT NOT NULL, Endpoint INTEGER NOT NULL CHECK(Endpoint IN (0,1)),
            X REAL NOT NULL CHECK(X BETWEEN 0 AND 65536), Y REAL NOT NULL CHECK(Y BETWEEN 0 AND 65536),
            TargetCanvasItemId TEXT NULL, Anchor INTEGER NOT NULL CHECK(Anchor BETWEEN 0 AND 4), PRIMARY KEY(DecorationId,Endpoint),
            FOREIGN KEY(DecorationId,TemplateId) REFERENCES PageTemplateDecorations(Id,TemplateId) ON DELETE CASCADE,
            FOREIGN KEY(TargetCanvasItemId,TemplateId) REFERENCES PageTemplateCanvasItems(Id,TemplateId) DEFERRABLE INITIALLY DEFERRED
        );
        CREATE TABLE PageTemplateStrokePoints (
            DecorationId TEXT NOT NULL REFERENCES PageTemplateDecorations(Id) ON DELETE CASCADE,
            PointIndex INTEGER NOT NULL CHECK(PointIndex BETWEEN 0 AND 8191),
            X REAL NOT NULL CHECK(X BETWEEN 0 AND 65536), Y REAL NOT NULL CHECK(Y BETWEEN 0 AND 65536), PRIMARY KEY(DecorationId,PointIndex)
        );
        """;
}
