namespace PersonalWorkspace.Data.Migrations;

internal static class ActivityHistoryMigration
{
    public static DatabaseMigration Definition { get; } = new(21, "Create Profile activity history", """
        CREATE TABLE AuditHistoryState(Id INTEGER PRIMARY KEY CHECK(Id=1), Epoch TEXT NOT NULL);
        INSERT INTO AuditHistoryState VALUES(1,lower(hex(randomblob(16))));
        CREATE TABLE AuditEvents(
            Sequence INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE, OperationId TEXT NOT NULL UNIQUE,
            OccurredAtUtcTicks INTEGER NOT NULL, Action INTEGER NOT NULL CHECK(Action BETWEEN 0 AND 14),
            EntityType INTEGER NOT NULL CHECK(EntityType BETWEEN 1 AND 20), EntityId TEXT NOT NULL,
            Title TEXT NOT NULL CHECK(length(Title)<=256), TitleKey TEXT NOT NULL, TitleTruncated INTEGER NOT NULL CHECK(TitleTruncated IN(0,1)),
            ParentId TEXT NULL, ContextDate TEXT NULL, ChangeCount INTEGER NOT NULL CHECK(ChangeCount>0), FormatVersion INTEGER NOT NULL CHECK(FormatVersion>0));
        CREATE INDEX IX_AuditEvents_Time ON AuditEvents(OccurredAtUtcTicks DESC,Sequence DESC);
        CREATE INDEX IX_AuditEvents_Type ON AuditEvents(EntityType,OccurredAtUtcTicks DESC,Sequence DESC);
        CREATE INDEX IX_AuditEvents_Action ON AuditEvents(Action,OccurredAtUtcTicks DESC,Sequence DESC);
        CREATE INDEX IX_AuditEvents_Title ON AuditEvents(TitleKey,OccurredAtUtcTicks DESC,Sequence DESC);
        CREATE TABLE AuditEventTargets(
            EventSequence INTEGER NOT NULL REFERENCES AuditEvents(Sequence) ON DELETE CASCADE,
            Ordinal INTEGER NOT NULL CHECK(Ordinal>=0), EntityType INTEGER NOT NULL CHECK(EntityType BETWEEN 1 AND 20), EntityId TEXT NOT NULL,
            Title TEXT NOT NULL CHECK(length(Title)<=256), TitleTruncated INTEGER NOT NULL CHECK(TitleTruncated IN(0,1)),
            ParentId TEXT NULL, ContextDate TEXT NULL, Role TEXT NOT NULL CHECK(length(Role)<=32),
            PRIMARY KEY(EventSequence,Ordinal));
        CREATE INDEX IX_AuditTargets_Entity ON AuditEventTargets(EntityId,EntityType,EventSequence);
        CREATE TABLE AuditEventChanges(
            EventSequence INTEGER NOT NULL, Ordinal INTEGER NOT NULL CHECK(Ordinal>=0), TargetOrdinal INTEGER NOT NULL,
            Field TEXT NOT NULL CHECK(length(Field)<=256), BeforeKind INTEGER NOT NULL, BeforeState INTEGER NOT NULL CHECK(BeforeState BETWEEN 0 AND 4),
            BeforeValue TEXT NULL CHECK(length(BeforeValue)<=512), BeforeLength INTEGER NULL,
            AfterKind INTEGER NOT NULL, AfterState INTEGER NOT NULL CHECK(AfterState BETWEEN 0 AND 4),
            AfterValue TEXT NULL CHECK(length(AfterValue)<=512), AfterLength INTEGER NULL,
            PRIMARY KEY(EventSequence,Ordinal),
            FOREIGN KEY(EventSequence,TargetOrdinal) REFERENCES AuditEventTargets(EventSequence,Ordinal) ON DELETE CASCADE,
            CHECK(BeforeKind BETWEEN 0 AND 11 AND AfterKind BETWEEN 0 AND 11),
            CHECK((BeforeState IN(0,1,3) AND BeforeValue IS NULL) OR (BeforeState IN(2,4) AND BeforeValue IS NOT NULL)),
            CHECK((AfterState IN(0,1,3) AND AfterValue IS NULL) OR (AfterState IN(2,4) AND AfterValue IS NOT NULL)));
        CREATE TRIGGER TR_AuditEvent_Immutable BEFORE UPDATE ON AuditEvents BEGIN SELECT RAISE(ABORT,'History is immutable'); END;
        CREATE TRIGGER TR_AuditTarget_Immutable BEFORE UPDATE ON AuditEventTargets BEGIN SELECT RAISE(ABORT,'History is immutable'); END;
        CREATE TRIGGER TR_AuditChange_Immutable BEFORE UPDATE ON AuditEventChanges BEGIN SELECT RAISE(ABORT,'History is immutable'); END;
        """);
}
