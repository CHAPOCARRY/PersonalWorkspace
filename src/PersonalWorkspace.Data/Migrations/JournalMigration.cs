namespace PersonalWorkspace.Data.Migrations;

internal static class JournalMigration
{
    public static DatabaseMigration Definition { get; } = new(9, "Create configurable journals", """
        CREATE TABLE Journals (
            ItemId TEXT NOT NULL PRIMARY KEY REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
            Description TEXT NOT NULL DEFAULT ''
        );
        CREATE TRIGGER TR_Journals_Identity BEFORE INSERT ON Journals
            WHEN NOT EXISTS(SELECT 1 FROM WorkspaceItems WHERE Id=NEW.ItemId AND ItemType=4)
            BEGIN SELECT RAISE(ABORT,'Journal requires a Journal WorkspaceItem'); END;
        CREATE TABLE JournalFields (
            Id TEXT NOT NULL PRIMARY KEY,
            JournalId TEXT NOT NULL REFERENCES Journals(ItemId) ON DELETE CASCADE,
            Name TEXT NOT NULL CHECK(length(trim(Name))>0),
            FieldType INTEGER NOT NULL CHECK(FieldType BETWEEN 0 AND 13),
            SortOrder INTEGER NOT NULL,
            CurrencyCode TEXT NULL,
            ScaleMin INTEGER NULL,
            ScaleMax INTEGER NULL,
            HistoryLocked INTEGER NOT NULL DEFAULT 0 CHECK(HistoryLocked IN (0,1)),
            CHECK((FieldType=4 AND CurrencyCode IS NOT NULL AND CurrencyCode GLOB '[A-Z][A-Z][A-Z]') OR (FieldType<>4 AND CurrencyCode IS NULL)),
            CHECK((FieldType=7 AND ScaleMin IS NOT NULL AND ScaleMax IS NOT NULL AND ScaleMax>ScaleMin) OR (FieldType<>7 AND ScaleMin IS NULL AND ScaleMax IS NULL)),
            UNIQUE(Id,JournalId,FieldType)
        );
        CREATE INDEX IX_JournalFields_Order ON JournalFields(JournalId,SortOrder,Id);
        CREATE TABLE JournalFieldOptions (
            Id TEXT NOT NULL PRIMARY KEY,
            FieldId TEXT NOT NULL REFERENCES JournalFields(Id) ON DELETE CASCADE,
            Label TEXT NOT NULL CHECK(length(trim(Label))>0),
            SortOrder INTEGER NOT NULL,
            UNIQUE(Id,FieldId)
        );
        CREATE INDEX IX_JournalOptions_Order ON JournalFieldOptions(FieldId,SortOrder,Id);
        CREATE TABLE JournalEntries (
            Id TEXT NOT NULL PRIMARY KEY,
            JournalId TEXT NOT NULL REFERENCES Journals(ItemId) ON DELETE CASCADE,
            EntryDate TEXT NOT NULL CHECK(length(EntryDate)=10),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            UNIQUE(JournalId,EntryDate),
            UNIQUE(Id,JournalId)
        );
        CREATE INDEX IX_JournalEntries_Date ON JournalEntries(EntryDate,JournalId);
        CREATE TABLE JournalValues (
            EntryId TEXT NOT NULL,
            FieldId TEXT NOT NULL,
            JournalId TEXT NOT NULL,
            FieldType INTEGER NOT NULL,
            TextValue TEXT NULL,
            DecimalValue TEXT NULL,
            IntegerValue INTEGER NULL CHECK(IntegerValue IS NULL OR typeof(IntegerValue)='integer'),
            DateValue TEXT NULL CHECK(DateValue IS NULL OR length(DateValue)=10),
            TaskId TEXT NULL REFERENCES Tasks(ItemId) ON DELETE SET NULL,
            TrackerId TEXT NULL REFERENCES Trackers(ItemId) ON DELETE SET NULL,
            PRIMARY KEY(EntryId,FieldId),
            FOREIGN KEY(EntryId,JournalId) REFERENCES JournalEntries(Id,JournalId) ON DELETE CASCADE,
            FOREIGN KEY(FieldId,JournalId,FieldType) REFERENCES JournalFields(Id,JournalId,FieldType) ON DELETE CASCADE,
            CHECK((FieldType IN (0,1,11) AND TextValue IS NOT NULL) OR (FieldType NOT IN (0,1,11) AND TextValue IS NULL)),
            CHECK((FieldType IN (2,3,4) AND DecimalValue IS NOT NULL) OR (FieldType NOT IN (2,3,4) AND DecimalValue IS NULL)),
            CHECK((FieldType IN (5,6,7) AND IntegerValue IS NOT NULL) OR (FieldType NOT IN (5,6,7) AND IntegerValue IS NULL)),
            CHECK(FieldType<>5 OR IntegerValue BETWEEN 0 AND 922337203685),
            CHECK(FieldType<>6 OR IntegerValue IN (0,1)),
            CHECK((FieldType=10 AND DateValue IS NOT NULL) OR (FieldType<>10 AND DateValue IS NULL)),
            CHECK(FieldType=12 OR TaskId IS NULL),
            CHECK(FieldType=13 OR TrackerId IS NULL)
        );
        CREATE INDEX IX_JournalValues_Field ON JournalValues(FieldId,EntryId);
        CREATE INDEX IX_JournalValues_Task ON JournalValues(TaskId) WHERE TaskId IS NOT NULL;
        CREATE INDEX IX_JournalValues_Tracker ON JournalValues(TrackerId) WHERE TrackerId IS NOT NULL;
        CREATE TABLE JournalSelections (
            EntryId TEXT NOT NULL,
            FieldId TEXT NOT NULL,
            OptionId TEXT NOT NULL,
            PRIMARY KEY(EntryId,FieldId,OptionId),
            FOREIGN KEY(EntryId,FieldId) REFERENCES JournalValues(EntryId,FieldId) ON DELETE CASCADE,
            FOREIGN KEY(OptionId,FieldId) REFERENCES JournalFieldOptions(Id,FieldId) ON DELETE NO ACTION
        );
        CREATE INDEX IX_JournalSelections_Option ON JournalSelections(OptionId,FieldId);
        CREATE TRIGGER TR_JournalSelections_Type BEFORE INSERT ON JournalSelections
            WHEN NOT EXISTS(SELECT 1 FROM JournalValues v WHERE v.EntryId=NEW.EntryId AND v.FieldId=NEW.FieldId
                AND (v.FieldType=9 OR (v.FieldType=8 AND NOT EXISTS(SELECT 1 FROM JournalSelections s WHERE s.EntryId=NEW.EntryId AND s.FieldId=NEW.FieldId))))
            BEGIN SELECT RAISE(ABORT,'Selection must match its field type'); END;
        """);
}
