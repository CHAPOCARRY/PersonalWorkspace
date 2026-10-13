namespace PersonalWorkspace.Data.Migrations;

internal static class RichContentMigration
{
    public static DatabaseMigration Definition { get; } = new(15, "Create shared rich content and assets", """
        CREATE TABLE Assets (
            Id TEXT NOT NULL PRIMARY KEY,
            StorageRelativePath TEXT NOT NULL UNIQUE CHECK(length(StorageRelativePath) BETWEEN 36 AND 48 AND StorageRelativePath NOT LIKE '%..%' AND instr(StorageRelativePath,':')=0 AND instr(StorageRelativePath,char(92))=0 AND substr(StorageRelativePath,1,1)<>'/'),
            OriginalFileName TEXT NOT NULL CHECK(length(OriginalFileName) BETWEEN 1 AND 255),
            MediaType TEXT NOT NULL,
            ByteLength INTEGER NOT NULL CHECK(ByteLength>=0),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64 AND ContentHash NOT GLOB '*[^0-9A-F]*'),
            CreatedAtUtc TEXT NOT NULL,
            PixelWidth INTEGER NULL, PixelHeight INTEGER NULL,
            PendingDelete INTEGER NOT NULL DEFAULT 0 CHECK(PendingDelete IN(0,1)),
            CHECK((PixelWidth IS NULL AND PixelHeight IS NULL) OR (PixelWidth>0 AND PixelHeight>0 AND PixelWidth IS NOT NULL AND PixelHeight IS NOT NULL))
        );
        CREATE INDEX IX_Assets_Pending ON Assets(PendingDelete) WHERE PendingDelete=1;
        CREATE TABLE AssetReferences (
            Id TEXT NOT NULL PRIMARY KEY,
            AssetId TEXT NOT NULL REFERENCES Assets(Id) ON DELETE RESTRICT,
            OwnerItemId TEXT NOT NULL REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
            Role INTEGER NOT NULL CHECK(Role=0),
            SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE INDEX IX_AssetReferences_Asset ON AssetReferences(AssetId);
        CREATE INDEX IX_AssetReferences_Owner ON AssetReferences(OwnerItemId,SortOrder,Id);
        CREATE TABLE AssetImports (
            AssetId TEXT NOT NULL PRIMARY KEY,
            StorageRelativePath TEXT NOT NULL UNIQUE
        );
        CREATE TRIGGER TR_Asset_Immutable BEFORE UPDATE OF Id,StorageRelativePath,OriginalFileName,MediaType,ByteLength,ContentHash,CreatedAtUtc,PixelWidth,PixelHeight ON Assets
            BEGIN SELECT RAISE(ABORT,'Asset bytes and metadata are immutable'); END;
        CREATE TRIGGER TR_Asset_Pending BEFORE UPDATE OF PendingDelete ON Assets
            WHEN NEW.PendingDelete<>1 OR EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=OLD.Id)
            BEGIN SELECT RAISE(ABORT,'Only orphan assets may be marked for cleanup'); END;
        CREATE TRIGGER TR_AssetReference_Insert BEFORE INSERT ON AssetReferences
            WHEN NOT EXISTS(SELECT 1 FROM Assets WHERE Id=NEW.AssetId AND PendingDelete=0)
              OR NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.OwnerItemId AND ArchivedAtUtc IS NULL AND DeletedAtUtc IS NULL)
            BEGIN SELECT RAISE(ABORT,'Choose an available Asset and active owner'); END;
        CREATE TRIGGER TR_AssetReference_Immutable BEFORE UPDATE ON AssetReferences
            BEGIN SELECT RAISE(ABORT,'Remove and create references explicitly'); END;
        CREATE TRIGGER TR_AssetReference_Cleanup AFTER DELETE ON AssetReferences
            WHEN NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=OLD.AssetId)
            BEGIN UPDATE Assets SET PendingDelete=1 WHERE Id=OLD.AssetId; END;
        CREATE TABLE RichTextDocuments (
            Id TEXT NOT NULL PRIMARY KEY,
            Content TEXT NOT NULL CHECK(length(Content)<=1000000),
            Format TEXT NOT NULL CHECK(Format='markdown-subset'),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        CREATE TABLE RichTextReferences (
            Id TEXT NOT NULL PRIMARY KEY,
            DocumentId TEXT NOT NULL REFERENCES RichTextDocuments(Id) ON DELETE RESTRICT,
            OwnerItemId TEXT NOT NULL REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE INDEX IX_RichTextReferences_Document ON RichTextReferences(DocumentId);
        CREATE INDEX IX_RichTextReferences_Owner ON RichTextReferences(OwnerItemId,CreatedAtUtc,Id);
        CREATE TRIGGER TR_RichText_Identity BEFORE UPDATE OF Id,Format,FormatVersion,CreatedAtUtc ON RichTextDocuments
            BEGIN SELECT RAISE(ABORT,'Document identity and format are immutable'); END;
        CREATE TRIGGER TR_RichTextReference_Insert BEFORE INSERT ON RichTextReferences
            WHEN NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.OwnerItemId AND ArchivedAtUtc IS NULL AND DeletedAtUtc IS NULL)
            BEGIN SELECT RAISE(ABORT,'Choose an active owner'); END;
        CREATE TRIGGER TR_RichTextReference_Immutable BEFORE UPDATE ON RichTextReferences
            BEGIN SELECT RAISE(ABORT,'Remove and create references explicitly'); END;
        CREATE TRIGGER TR_RichTextReference_Cleanup AFTER DELETE ON RichTextReferences
            WHEN NOT EXISTS(SELECT 1 FROM RichTextReferences WHERE DocumentId=OLD.DocumentId)
            BEGIN DELETE FROM RichTextDocuments WHERE Id=OLD.DocumentId; END;
        """);
}
