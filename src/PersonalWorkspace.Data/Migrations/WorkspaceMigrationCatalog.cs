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
            """)
    ];
}
