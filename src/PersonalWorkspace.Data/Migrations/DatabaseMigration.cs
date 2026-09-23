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
            """)
    ];
}
