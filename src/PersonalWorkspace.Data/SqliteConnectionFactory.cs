using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteConnectionFactory(IApplicationPaths paths)
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        => await OpenDatabaseAsync(paths.Database, true, cancellationToken);

    internal static async Task<SqliteConnection> OpenDatabaseAsync(string database, bool create, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            DefaultTimeout = 10,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.CreateCollation("PROFILE_NAME", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
