using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteOrganizationRepository : IOrganizationRepository
{
    public async Task<OrganizationSnapshot> GetAsync(WorkspaceContext workspace, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var tags = new List<Tag>();
        var spaces = new List<Space>();
        using (var command = Command(connection, transaction, "SELECT Id, Name, Color, CreatedAtUtc, UpdatedAtUtc FROM Tags ORDER BY Name;"))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tags.Add(new(Id(reader, 0), reader.GetString(1), Color(reader, 2), Utc(reader, 3), Utc(reader, 4)));
        using (var command = Command(connection, transaction, "SELECT Id, Name, Description, Icon, Color, SortOrder, CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc FROM Spaces ORDER BY SortOrder, Name, Id;"))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) spaces.Add(new(Id(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Color(reader, 4), reader.GetInt32(5), Utc(reader, 6), Utc(reader, 7), reader.IsDBNull(8) ? null : Utc(reader, 8)));
        var itemTags = await ReadAssignments(connection, transaction, OrganizationKind.Tag, cancellationToken);
        var itemSpaces = await ReadAssignments(connection, transaction, OrganizationKind.Space, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(tags, spaces, itemTags, itemSpaces);
    }

    public async Task SaveAsync(WorkspaceContext workspace, OrganizationKind kind, Guid id, OrganizationDraft draft, bool create, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (table, _, _) = Tables(kind);
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!create) now = await NextTimestamp(connection, transaction, table, id, now, cancellationToken);
        using var command = Command(connection, transaction, create
            ? kind == OrganizationKind.Tag
                ? "INSERT INTO Tags (Id, Name, Color, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $name, $color, $now, $now);"
                : """
                    INSERT INTO Spaces (Id, Name, Description, Icon, Color, SortOrder, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($id, $name, $description, $icon, $color, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Spaces), $now, $now);
                    """
            : kind == OrganizationKind.Tag
                ? "UPDATE Tags SET Name = $name, Color = $color, UpdatedAtUtc = $now WHERE Id = $id AND (Name COLLATE BINARY != $name OR Color IS NOT $color);"
                : """
                    UPDATE Spaces SET Name = $name, Description = $description, Icon = $icon, Color = $color, UpdatedAtUtc = $now
                    WHERE Id = $id AND (Name COLLATE BINARY != $name OR Description != $description OR Icon != $icon OR Color IS NOT $color);
                    """);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", draft.Name);
        command.Parameters.AddWithValue("$color", draft.Color == OrganizationColor.None ? DBNull.Value : (object)(int)draft.Color);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        if (kind == OrganizationKind.Space)
        {
            command.Parameters.AddWithValue("$description", draft.Description);
            command.Parameters.AddWithValue("$icon", draft.Icon);
        }
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        { throw new OrganizationValidationException("This name is already in use."); }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTagAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(connection, transaction, "DELETE FROM Tags WHERE Id = $id;");
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken); // Cascades only assignments, never WorkspaceItems.
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ArchiveSpaceAsync(WorkspaceContext workspace, Guid id, bool archive, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        now = await NextTimestamp(connection, transaction, "Spaces", id, now, cancellationToken);
        using var command = Command(connection, transaction, """
            UPDATE Spaces SET ArchivedAtUtc = $archived, UpdatedAtUtc = $now WHERE Id = $id
            AND (($archived IS NULL AND ArchivedAtUtc IS NOT NULL) OR ($archived IS NOT NULL AND ArchivedAtUtc IS NULL));
            """);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$archived", archive ? now.ToUniversalTime().ToString("O") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AssignAsync(WorkspaceContext workspace, Guid itemId, OrganizationKind kind, Guid entityId, bool assigned, CancellationToken cancellationToken)
    {
        var (table, relationships, column) = Tables(kind);
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var item = Command(connection, transaction, "SELECT COUNT(*) FROM WorkspaceItems WHERE Id = $id AND DeletedAtUtc IS NULL;"))
        {
            item.Parameters.AddWithValue("$id", itemId.ToString("D"));
            if ((long)(await item.ExecuteScalarAsync(cancellationToken))! != 1)
                throw new OrganizationValidationException("This item is unavailable. Restore it from Trash before changing assignments.");
        }
        using (var entity = Command(connection, transaction, $"SELECT COUNT(*) FROM {table} WHERE Id = $id" +
            (assigned && kind == OrganizationKind.Space ? " AND ArchivedAtUtc IS NULL;" : ";")))
        {
            entity.Parameters.AddWithValue("$id", entityId.ToString("D"));
            if ((long)(await entity.ExecuteScalarAsync(cancellationToken))! != 1)
                throw new OrganizationValidationException("This tag or space is unavailable. Restore archived spaces before assigning them.");
        }
        using var command = Command(connection, transaction, assigned
            ? $"INSERT INTO {relationships} (ItemId, {column}) VALUES ($item, $entity) ON CONFLICT(ItemId, {column}) DO NOTHING;"
            : $"DELETE FROM {relationships} WHERE ItemId = $item AND {column} = $entity;");
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        command.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // SQL identifiers come only from this closed enum mapping, never names or user input.
    private static (string Table, string Relationships, string Column) Tables(OrganizationKind kind) => kind switch
    {
        OrganizationKind.Tag => ("Tags", "ItemTags", "TagId"),
        OrganizationKind.Space => ("Spaces", "ItemSpaces", "SpaceId"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static async Task<IReadOnlyList<ItemAssignment>> ReadAssignments(SqliteConnection connection, SqliteTransaction transaction, OrganizationKind kind, CancellationToken cancellationToken)
    {
        var (_, table, column) = Tables(kind);
        using var command = Command(connection, transaction, $"SELECT ItemId, {column} FROM {table};");
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ItemAssignment>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Id(reader, 0), Id(reader, 1)));
        return result;
    }
    private static async Task<DateTimeOffset> NextTimestamp(SqliteConnection connection, SqliteTransaction transaction, string table, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, $"SELECT UpdatedAtUtc FROM {table} WHERE Id = $id;");
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var previous = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new OrganizationValidationException("This tag or space is no longer available.");
        var timestamp = DateTimeOffset.Parse(previous, CultureInfo.InvariantCulture);
        return now > timestamp ? now : timestamp.AddTicks(1);
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
    private static Guid Id(SqliteDataReader reader, int index) => Guid.ParseExact(reader.GetString(index), "D");
    private static DateTimeOffset Utc(SqliteDataReader reader, int index) => DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private static OrganizationColor Color(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? OrganizationColor.None : (OrganizationColor)reader.GetInt32(index);
    private static Task<SqliteConnection> OpenAsync(WorkspaceContext workspace, CancellationToken cancellationToken) =>
        SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, cancellationToken);
}
