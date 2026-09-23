using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteProfileRepository(SqliteConnectionFactory connections) : IProfileRepository
{
    private const string DeletionPrefix = "profiles.pendingDeletion.";

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, FolderName, CreatedAtUtc, LastOpenedAtUtc, SortOrder FROM Profiles ORDER BY SortOrder, CreatedAtUtc, Id;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profiles = new List<Profile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.ParseExact(reader.GetString(0), "D");
            if (id == Guid.Empty || reader.GetString(2) != id.ToString("D"))
                throw new InvalidDataException("Invalid profile folder identity.");
            profiles.Add(new Profile(id, reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt32(5)));
        }
        return profiles;
    }

    public async Task CreateAndSelectAsync(Profile profile, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Profiles (Id, Name, FolderName, CreatedAtUtc, LastOpenedAtUtc, SortOrder) VALUES ($id, $name, $folder, $created, $opened, $sort);";
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$folder", profile.FolderName);
        command.Parameters.AddWithValue("$created", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$opened", profile.LastOpenedAtUtc!.Value.ToString("O"));
        command.Parameters.AddWithValue("$sort", profile.SortOrder);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        { throw new ProfileValidationException("A profile with that name already exists."); }
        await SelectWithinTransactionAsync(connection, transaction, profile, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Profiles SET Name = $name WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ProfileValidationException("This profile is no longer available.");
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        { throw new ProfileValidationException("A profile with that name already exists."); }
    }

    public async Task SelectAsync(Profile profile, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await SelectWithinTransactionAsync(connection, transaction, profile, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAndSelectAsync(Guid id, Profile? replacement, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Profiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ProfileValidationException("This profile is no longer available.");
        await SqliteSettingsService.WriteAsync(connection, transaction, DeletionPrefix + id.ToString("D"), true, cancellationToken);
        await SelectWithinTransactionAsync(connection, transaction, replacement, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SelectWithinTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, Profile? profile, CancellationToken cancellationToken)
    {
        if (profile is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Profiles SET LastOpenedAtUtc = $opened WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$opened", profile.LastOpenedAtUtc!.Value.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ProfileValidationException("This profile is no longer available.");
        }
        await SqliteSettingsService.WriteAsync(connection, transaction, SettingKeys.LastOpenedProfileId, profile?.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingDeletionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key FROM AppSettings WHERE Key LIKE $prefix;";
        command.Parameters.AddWithValue("$prefix", DeletionPrefix + "%");
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(Guid.ParseExact(reader.GetString(0)[DeletionPrefix.Length..], "D"));
        return ids;
    }

    public async Task CompleteDeletionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", DeletionPrefix + id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
