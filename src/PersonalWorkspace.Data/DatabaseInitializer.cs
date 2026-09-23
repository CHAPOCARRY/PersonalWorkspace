using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data.Migrations;

namespace PersonalWorkspace.Data;

public sealed class DatabaseInitializer(
    IApplicationPaths paths,
    SqliteConnectionFactory connections,
    IEnumerable<DatabaseMigration> migrations,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Database initialization started");
            paths.EnsureDirectories();
            var ordered = migrations.OrderBy(m => m.Version).ToArray();
            if (ordered.Any(m => m.Version <= 0) || ordered.Select(m => m.Version).Distinct().Count() != ordered.Length)
                throw new InvalidOperationException("Migration versions must be unique positive integers.");

            await using var connection = await connections.OpenAsync(cancellationToken);
            // An immediate write transaction serializes competing startup migrations.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Version INTEGER NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT Version FROM SchemaMigrations ORDER BY Version;";
            var applied = new HashSet<int>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetInt32(0));
            if (applied.Except(ordered.Select(m => m.Version)).Any())
                throw new InvalidOperationException("The database has migrations unknown to this application version.");

            foreach (var migration in ordered.Where(m => !applied.Contains(m.Version)))
            {
                logger.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.Name);
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText = "INSERT INTO SchemaMigrations (Version, Name, AppliedAtUtc) VALUES ($version, $name, $utc);";
                command.Parameters.AddWithValue("$version", migration.Version);
                command.Parameters.AddWithValue("$name", migration.Name);
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.Parameters.Clear();
            }
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Database initialization completed");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database initialization failed; pending migrations rolled back");
            throw;
        }
    }
}
