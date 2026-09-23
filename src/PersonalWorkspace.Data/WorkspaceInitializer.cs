using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data.Migrations;

namespace PersonalWorkspace.Data;

public sealed class WorkspaceInitializer(IApplicationPaths paths, ILogger<WorkspaceInitializer> logger) : IWorkspaceInitializer
{
    public async Task InitializeAsync(Guid id, bool create, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(paths.WorkspaceDatabase(id), create, cancellationToken);
            if (!create)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaMigrations';";
                if ((long)(await check.ExecuteScalarAsync(cancellationToken))! != 1)
                    throw new InvalidDataException("Workspace migration ledger is missing.");
            }
            // No workspace entities or migrations yet. The shared runner establishes the ledger.
            await new MigrationRunner([], logger).ApplyAsync(connection, cancellationToken);
            logger.LogInformation("Workspace database initialized for profile {ProfileId}", id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Workspace database initialization failed for profile {ProfileId}", id);
            throw;
        }
    }
}
