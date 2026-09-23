namespace PersonalWorkspace.Data.Migrations;

public sealed record DatabaseMigration(int Version, string Name, string Sql);

public static class MigrationCatalog
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(1, "Create application settings", """
            CREATE TABLE AppSettings (
                Key TEXT NOT NULL PRIMARY KEY,
                ValueJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """),
        new(2, "Create local profiles", """
            CREATE TABLE Profiles (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL COLLATE PROFILE_NAME UNIQUE CHECK(length(trim(Name)) > 0),
                FolderName TEXT NOT NULL UNIQUE CHECK(FolderName = Id),
                CreatedAtUtc TEXT NOT NULL,
                LastOpenedAtUtc TEXT NULL,
                SortOrder INTEGER NOT NULL
            );
            """)
    ];
}
