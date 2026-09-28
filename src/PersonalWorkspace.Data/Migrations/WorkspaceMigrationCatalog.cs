namespace PersonalWorkspace.Data.Migrations;

public static class WorkspaceMigrationCatalog
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(1, "Create task core", """
            CREATE TABLE WorkspaceItems (
                Id TEXT NOT NULL PRIMARY KEY,
                ItemType INTEGER NOT NULL,
                Title TEXT NOT NULL CHECK(length(trim(Title)) > 0),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                ArchivedAtUtc TEXT NULL,
                DeletedAtUtc TEXT NULL
            );
            CREATE TABLE Tasks (
                ItemId TEXT NOT NULL PRIMARY KEY REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
                Description TEXT NOT NULL DEFAULT '',
                Status INTEGER NOT NULL DEFAULT 0 CHECK(Status BETWEEN 0 AND 3),
                Priority INTEGER NOT NULL DEFAULT 0 CHECK(Priority BETWEEN 0 AND 4),
                ScheduledDate TEXT NULL CHECK(ScheduledDate IS NULL OR
                    (length(ScheduledDate) = 10 AND ScheduledDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'))
            );
            CREATE INDEX IX_WorkspaceItems_Collection ON WorkspaceItems(ItemType, DeletedAtUtc, ArchivedAtUtc);
            CREATE INDEX IX_Tasks_ScheduledDate ON Tasks(ScheduledDate) WHERE ScheduledDate IS NOT NULL;
            """),
        new(2, "Create tags and spaces", """
            CREATE TABLE Tags (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL COLLATE WORKSPACE_NAME UNIQUE CHECK(length(trim(Name)) > 0),
                Color INTEGER NULL CHECK(Color BETWEEN 1 AND 4),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE Spaces (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL COLLATE WORKSPACE_NAME UNIQUE CHECK(length(trim(Name)) > 0),
                Description TEXT NOT NULL DEFAULT '',
                Icon TEXT NOT NULL DEFAULT '',
                Color INTEGER NULL CHECK(Color BETWEEN 1 AND 4),
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                ArchivedAtUtc TEXT NULL
            );
            CREATE TABLE ItemTags (
                ItemId TEXT NOT NULL REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
                TagId TEXT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY(ItemId, TagId)
            );
            CREATE TABLE ItemSpaces (
                ItemId TEXT NOT NULL REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
                SpaceId TEXT NOT NULL REFERENCES Spaces(Id) ON DELETE CASCADE,
                PRIMARY KEY(ItemId, SpaceId)
            );
            CREATE INDEX IX_ItemTags_TagId ON ItemTags(TagId, ItemId);
            CREATE INDEX IX_ItemSpaces_SpaceId ON ItemSpaces(SpaceId, ItemId);
            """),
        new(3, "Create calendar events", """
            CREATE TABLE Events (
                ItemId TEXT NOT NULL PRIMARY KEY REFERENCES WorkspaceItems(Id) ON DELETE CASCADE,
                AllDay INTEGER NOT NULL CHECK(AllDay IN (0, 1)),
                StartDate TEXT NOT NULL CHECK(length(StartDate) = 10 AND StartDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                StartTime TEXT NULL,
                EndDate TEXT NOT NULL CHECK(length(EndDate) = 10 AND EndDate GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                EndTime TEXT NULL,
                CHECK(EndDate >= StartDate),
                CHECK((AllDay = 1 AND StartTime IS NULL AND EndTime IS NULL) OR
                    (AllDay = 0 AND StartTime IS NOT NULL AND EndTime IS NOT NULL AND
                     length(StartTime) = 16 AND length(EndTime) = 16 AND
                     StartTime >= '00:00:00.0000000' AND StartTime <= '23:59:59.9999999' AND
                     EndTime >= '00:00:00.0000000' AND EndTime <= '23:59:59.9999999' AND
                     (EndDate > StartDate OR EndTime > StartTime)))
            );
            CREATE INDEX IX_Events_StartDate ON Events(StartDate);
            CREATE INDEX IX_Events_EndDate ON Events(EndDate);
            """),
        new(4, "Create subtasks and dependencies", """
            ALTER TABLE Tasks ADD COLUMN ParentTaskId TEXT NULL REFERENCES Tasks(ItemId) ON DELETE SET NULL;
            CREATE INDEX IX_Tasks_ParentTaskId ON Tasks(ParentTaskId) WHERE ParentTaskId IS NOT NULL;
            CREATE TABLE TaskDependencies (
                TaskId TEXT NOT NULL REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                DependsOnTaskId TEXT NOT NULL REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY(TaskId, DependsOnTaskId),
                CHECK(TaskId <> DependsOnTaskId)
            );
            CREATE INDEX IX_TaskDependencies_DependsOnTaskId ON TaskDependencies(DependsOnTaskId, TaskId);
            """),
        new(5, "Create task values", """
            CREATE TABLE TaskValues (
                ItemId TEXT NOT NULL PRIMARY KEY REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                ValueType INTEGER NOT NULL CHECK(ValueType BETWEEN 1 AND 5),
                Target TEXT NULL,
                Actual TEXT NULL,
                TargetSeconds INTEGER NULL,
                ActualSeconds INTEGER NULL,
                CurrencyCode TEXT NULL,
                Unit TEXT NULL,
                CHECK((ValueType = 4 AND Target IS NULL AND Actual IS NULL AND
                    TargetSeconds IS NOT NULL AND typeof(TargetSeconds) = 'integer' AND TargetSeconds BETWEEN 1 AND 922337203685 AND
                    (ActualSeconds IS NULL OR (typeof(ActualSeconds) = 'integer' AND ActualSeconds BETWEEN 0 AND 922337203685))) OR
                    (ValueType <> 4 AND Target IS NOT NULL AND length(Target) > 0 AND TargetSeconds IS NULL AND ActualSeconds IS NULL)),
                CHECK((ValueType = 3 AND CurrencyCode IS NOT NULL AND length(CurrencyCode) = 3 AND CurrencyCode GLOB '[A-Z][A-Z][A-Z]') OR
                    (ValueType <> 3 AND CurrencyCode IS NULL)),
                CHECK((ValueType = 5 AND Unit IS NOT NULL AND length(trim(Unit)) BETWEEN 1 AND 32) OR
                    (ValueType <> 5 AND Unit IS NULL))
            );
            """),
        new(6, "Create task recurrence and occurrences", """
            CREATE TABLE TaskRecurrenceRules (
                Id TEXT NOT NULL PRIMARY KEY,
                TaskId TEXT NOT NULL REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                Pattern INTEGER NOT NULL CHECK(Pattern BETWEEN 0 AND 3),
                StartDate TEXT NOT NULL,
                Interval INTEGER NOT NULL CHECK(Interval BETWEEN 1 AND 999),
                Weekdays INTEGER NOT NULL,
                MonthDay INTEGER NOT NULL,
                Ordinal INTEGER NOT NULL,
                Weekday INTEGER NOT NULL,
                EndDate TEXT NULL CHECK(EndDate IS NULL OR EndDate >= StartDate),
                FromDate TEXT NOT NULL,
                UntilDate TEXT NULL CHECK(UntilDate IS NULL OR UntilDate >= FromDate),
                Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)),
                CreatedAtUtc TEXT NOT NULL,
                UNIQUE(Id, TaskId),
                CHECK(Pattern <> 1 OR Weekdays BETWEEN 1 AND 127),
                CHECK(Pattern <> 2 OR MonthDay BETWEEN 1 AND 31),
                CHECK(Pattern <> 3 OR (Ordinal IN (-1,1,2,3,4,5) AND Weekday BETWEEN 0 AND 6))
            );
            CREATE INDEX IX_TaskRecurrenceRules_Task ON TaskRecurrenceRules(TaskId, Enabled, FromDate, UntilDate);
            CREATE TABLE TaskOccurrences (
                Id TEXT NOT NULL PRIMARY KEY,
                TaskId TEXT NOT NULL REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                SegmentId TEXT NOT NULL,
                SlotDate TEXT NOT NULL,
                OccurrenceDate TEXT NOT NULL,
                Status INTEGER NOT NULL CHECK(Status BETWEEN 0 AND 3),
                Actual TEXT NULL,
                IsSkipped INTEGER NOT NULL CHECK(IsSkipped IN (0,1)),
                IsOverride INTEGER NOT NULL CHECK(IsOverride IN (0,1)),
                IsSuppressed INTEGER NOT NULL CHECK(IsSuppressed IN (0,1)),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE(TaskId, SlotDate),
                FOREIGN KEY(SegmentId, TaskId) REFERENCES TaskRecurrenceRules(Id, TaskId) ON DELETE CASCADE
            );
            CREATE INDEX IX_TaskOccurrences_Date ON TaskOccurrences(OccurrenceDate, TaskId);
            CREATE INDEX IX_TaskOccurrences_Slot ON TaskOccurrences(SlotDate, TaskId);
            """),
        new(7, "Create occurrence carry calculations", """
            CREATE TABLE TaskCarrySettings (
                TaskId TEXT NOT NULL PRIMARY KEY REFERENCES Tasks(ItemId) ON DELETE CASCADE,
                CarryDeficit INTEGER NOT NULL DEFAULT 0 CHECK(CarryDeficit IN (0,1)),
                CarrySurplus INTEGER NOT NULL DEFAULT 0 CHECK(CarrySurplus IN (0,1)),
                HistoryLocked INTEGER NOT NULL DEFAULT 0 CHECK(HistoryLocked IN (0,1))
            );
            CREATE TABLE TaskOccurrenceCalculations (
                OccurrenceId TEXT NOT NULL PRIMARY KEY REFERENCES TaskOccurrences(Id) ON DELETE CASCADE,
                BaseTarget TEXT NOT NULL,
                CarryIn TEXT NOT NULL,
                EffectiveTarget TEXT NOT NULL,
                CarryOut TEXT NOT NULL
            );
            INSERT INTO TaskOccurrenceCalculations(OccurrenceId,BaseTarget,CarryIn,EffectiveTarget,CarryOut)
                SELECT o.Id,COALESCE(v.Target,CAST(v.TargetSeconds AS TEXT)),'0',
                    COALESCE(v.Target,CAST(v.TargetSeconds AS TEXT)),'0'
                FROM TaskOccurrences o JOIN TaskValues v ON v.ItemId=o.TaskId;
            INSERT INTO TaskCarrySettings(TaskId,HistoryLocked)
                SELECT v.ItemId,EXISTS(SELECT 1 FROM TaskOccurrences o WHERE o.TaskId=v.ItemId
                    AND (o.IsOverride=1 OR o.IsSkipped=1 OR o.Status<>0 OR o.Actual IS NOT NULL))
                FROM TaskValues v WHERE v.ValueType IN (1,3,4,5)
                    AND EXISTS(SELECT 1 FROM TaskRecurrenceRules r WHERE r.TaskId=v.ItemId AND r.Enabled=1);
            CREATE INDEX IX_TaskOccurrences_CarryPredecessor ON TaskOccurrences(TaskId,SlotDate)
                WHERE IsSkipped=0 AND IsSuppressed=0;
            """)
    ];
}
