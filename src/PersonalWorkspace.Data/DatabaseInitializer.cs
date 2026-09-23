using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data.Migrations;

namespace PersonalWorkspace.Data;

public sealed class DatabaseInitializer(IApplicationPaths paths, SqliteConnectionFactory connections,
    IEnumerable<DatabaseMigration> migrations, ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            paths.EnsureDirectories();
            await using var connection = await connections.OpenAsync(cancellationToken);
            await new MigrationRunner(migrations, logger).ApplyAsync(connection, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Global database initialization failed");
            throw;
        }
    }
}
