namespace PersonalWorkspace.Data.Migrations;

public static class TemplateMigration
{
    public static DatabaseMigration Definition { get; } = new(18, "Create definition templates", """
        CREATE TABLE Templates (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateType INTEGER NOT NULL CHECK(TemplateType IN (1,2,3)),
            Name TEXT NOT NULL CHECK(length(trim(Name)) BETWEEN 1 AND 200),
            Description TEXT NULL CHECK(Description IS NULL OR length(Description)<=50000),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            UNIQUE(Id,TemplateType)
        );
        CREATE INDEX IX_Templates_Catalog ON Templates(TemplateType,Name,Id);
        CREATE TRIGGER TR_Templates_Identity BEFORE UPDATE OF Id,TemplateType,CreatedAtUtc ON Templates
            WHEN NEW.Id<>OLD.Id OR NEW.TemplateType<>OLD.TemplateType OR NEW.CreatedAtUtc<>OLD.CreatedAtUtc
            BEGIN SELECT RAISE(ABORT,'Template identity is immutable'); END;
        CREATE TABLE TaskTemplateDefinitions (
            TemplateId TEXT NOT NULL PRIMARY KEY,
            TemplateType INTEGER NOT NULL DEFAULT 1 CHECK(TemplateType=1),
            Title TEXT NOT NULL CHECK(length(trim(Title))>0),
            Description TEXT NOT NULL,
            Priority INTEGER NOT NULL CHECK(Priority BETWEEN 0 AND 4),
            ValueType INTEGER NOT NULL CHECK(ValueType BETWEEN 0 AND 5),
            Target TEXT NULL,
            TargetSeconds INTEGER NULL CHECK(TargetSeconds IS NULL OR (typeof(TargetSeconds)='integer' AND TargetSeconds BETWEEN 1 AND 922337203685)),
            CurrencyCode TEXT NULL,
            Unit TEXT NULL,
            FOREIGN KEY(TemplateId,TemplateType) REFERENCES Templates(Id,TemplateType) ON DELETE CASCADE,
            CHECK((ValueType=0 AND Target IS NULL AND TargetSeconds IS NULL) OR (ValueType=4 AND Target IS NULL AND TargetSeconds IS NOT NULL) OR (ValueType IN (1,2,3,5) AND Target IS NOT NULL AND TargetSeconds IS NULL)),
            CHECK((ValueType=3 AND CurrencyCode IS NOT NULL AND CurrencyCode GLOB '[A-Z][A-Z][A-Z]') OR (ValueType<>3 AND CurrencyCode IS NULL)),
            CHECK((ValueType=5 AND Unit IS NOT NULL AND length(trim(Unit)) BETWEEN 1 AND 32) OR (ValueType<>5 AND Unit IS NULL))
        );
        CREATE TABLE ListTemplateDefinitions (
            TemplateId TEXT NOT NULL PRIMARY KEY,
            TemplateType INTEGER NOT NULL DEFAULT 2 CHECK(TemplateType=2),
            Title TEXT NOT NULL CHECK(length(trim(Title)) BETWEEN 1 AND 200),
            Description TEXT NOT NULL,
            ShowQuantity INTEGER NOT NULL CHECK(ShowQuantity IN (0,1)),
            ShowPrices INTEGER NOT NULL CHECK(ShowPrices IN (0,1)),
            CurrencyCode TEXT NULL CHECK(CurrencyCode IS NULL OR CurrencyCode GLOB '[A-Z][A-Z][A-Z]'),
            FOREIGN KEY(TemplateId,TemplateType) REFERENCES Templates(Id,TemplateType) ON DELETE CASCADE
        );
        CREATE TABLE ListTemplateItems (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES ListTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            Title TEXT NOT NULL CHECK(length(trim(Title)) BETWEEN 1 AND 200),
            Quantity TEXT NULL,
            UnitPrice TEXT NULL,
            Note TEXT NULL,
            Link TEXT NULL,
            SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),
            UNIQUE(TemplateId,SortOrder)
        );
        CREATE TABLE JournalTemplateDefinitions (
            TemplateId TEXT NOT NULL PRIMARY KEY,
            TemplateType INTEGER NOT NULL DEFAULT 3 CHECK(TemplateType=3),
            Title TEXT NOT NULL CHECK(length(trim(Title)) BETWEEN 1 AND 200),
            Description TEXT NOT NULL,
            FOREIGN KEY(TemplateId,TemplateType) REFERENCES Templates(Id,TemplateType) ON DELETE CASCADE
        );
        CREATE TABLE JournalTemplateFields (
            Id TEXT NOT NULL PRIMARY KEY,
            TemplateId TEXT NOT NULL REFERENCES JournalTemplateDefinitions(TemplateId) ON DELETE CASCADE,
            Name TEXT NOT NULL CHECK(length(trim(Name)) BETWEEN 1 AND 200),
            FieldType INTEGER NOT NULL CHECK(FieldType BETWEEN 0 AND 13),
            SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),
            CurrencyCode TEXT NULL,
            ScaleMin INTEGER NULL,
            ScaleMax INTEGER NULL,
            UNIQUE(TemplateId,SortOrder),
            CHECK((FieldType=4 AND CurrencyCode IS NOT NULL AND CurrencyCode GLOB '[A-Z][A-Z][A-Z]') OR (FieldType<>4 AND CurrencyCode IS NULL)),
            CHECK((FieldType=7 AND ScaleMin IS NOT NULL AND ScaleMax IS NOT NULL AND ScaleMax>ScaleMin) OR (FieldType<>7 AND ScaleMin IS NULL AND ScaleMax IS NULL))
        );
        CREATE TABLE JournalTemplateOptions (
            Id TEXT NOT NULL PRIMARY KEY,
            FieldId TEXT NOT NULL REFERENCES JournalTemplateFields(Id) ON DELETE CASCADE,
            Label TEXT NOT NULL COLLATE WORKSPACE_NAME CHECK(length(trim(Label)) BETWEEN 1 AND 200),
            SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),
            UNIQUE(FieldId,SortOrder), UNIQUE(FieldId,Label)
        );
        CREATE TRIGGER TR_JournalTemplateOptions_Type BEFORE INSERT ON JournalTemplateOptions
            WHEN NOT EXISTS(SELECT 1 FROM JournalTemplateFields WHERE Id=NEW.FieldId AND FieldType IN (8,9))
            BEGIN SELECT RAISE(ABORT,'Options require a Select or MultiSelect field'); END;
        CREATE TRIGGER TR_JournalTemplateOptions_Update BEFORE UPDATE ON JournalTemplateOptions
            WHEN NOT EXISTS(SELECT 1 FROM JournalTemplateFields WHERE Id=NEW.FieldId AND FieldType IN (8,9))
            BEGIN SELECT RAISE(ABORT,'Options require a Select or MultiSelect field'); END;
        CREATE TRIGGER TR_JournalTemplateFields_Options BEFORE UPDATE OF FieldType ON JournalTemplateFields
            WHEN NEW.FieldType NOT IN (8,9) AND EXISTS(SELECT 1 FROM JournalTemplateOptions WHERE FieldId=OLD.Id)
            BEGIN SELECT RAISE(ABORT,'Remove options before changing the field type'); END;
        """);
}
