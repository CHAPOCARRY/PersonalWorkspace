using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteSettingsService(SqliteConnectionFactory connections, ILogger<SqliteSettingsService> logger) : ISettingsService
{
    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            await using var connection = await connections.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ValueJson FROM AppSettings WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);
            if (await command.ExecuteScalarAsync(cancellationToken) is not string json) return fallback;
            try { return JsonSerializer.Deserialize<T>(json) ?? fallback; }
            catch (JsonException)
            {
                // Never include the stored JSON in diagnostics.
                logger.LogWarning("Invalid setting format; using default");
                return fallback;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Reading application settings failed");
            throw;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var json = JsonSerializer.Serialize(value);
            await using var connection = await connections.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AppSettings (Key, ValueJson, UpdatedAtUtc) VALUES ($key, $json, $utc)
                ON CONFLICT(Key) DO UPDATE SET ValueJson = excluded.ValueJson, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Writing application settings failed");
            throw;
        }
    }
}
