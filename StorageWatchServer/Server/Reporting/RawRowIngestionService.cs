using Microsoft.Data.Sqlite;
using StorageWatchServer.Server.Data;
using StorageWatchServer.Server.Models;
using StorageWatchServer.Server.Services;
using StorageWatchServer.Services.Logging;

namespace StorageWatchServer.Server.Reporting;

/// <summary>
/// Service for inserting raw drive rows into the database exactly as received.
/// Performs no transformations, normalization, or deduplication.
/// </summary>
public class RawRowIngestionService
{
    private readonly ServerOptions _options;
    private readonly RollingFileLogger? _logger;
    private readonly ServerDatabaseShutdownCoordinator _databaseShutdownCoordinator;

    public RawRowIngestionService(
        ServerOptions options,
        RollingFileLogger? logger = null,
        ServerDatabaseShutdownCoordinator? databaseShutdownCoordinator = null)
    {
        _options = options;
        _logger = logger;
        _databaseShutdownCoordinator = databaseShutdownCoordinator ?? new ServerDatabaseShutdownCoordinator();
    }

    private string GetConnectionString()
    {
        if (_options.DatabasePath.Contains("mode=memory") || _options.DatabasePath.StartsWith("file:"))
        {
            return $"Data Source={_options.DatabasePath}";
        }

        var databasePath = Path.GetFullPath(_options.DatabasePath);
        return $"Data Source={databasePath}";
    }

    /// <summary>
    /// Inserts a batch of raw drive rows into the database.
    /// </summary>
    public async Task IngestRawRowsAsync(string machineName, List<RawDriveRow> rows)
    {
        if (string.IsNullOrWhiteSpace(machineName) || rows == null || rows.Count == 0)
        {
            throw new ArgumentException("Machine name must be provided and rows list must not be empty.");
        }

        var machineNameForLog = machineName
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

        await using var operation = await _databaseShutdownCoordinator.BeginOperationAsync();

        _logger?.Log($"[INGEST] Received report from {machineNameForLog} with {rows.Count} rows");

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();

        await using (var command = new SqliteCommand("PRAGMA foreign_keys = ON;", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        _logger?.Log($"[DB] Inserting {rows.Count} RawDriveRows for {machineNameForLog}");

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        try
        {
            var insertSql = @"
                INSERT INTO RawDriveRows
                    (MachineName, DriveLetter, TotalSpaceGb, UsedSpaceGb, FreeSpaceGb, PercentFree, Timestamp)
                VALUES
                    (@machineName, @driveLetter, @totalSpaceGb, @usedSpaceGb, @freeSpaceGb, @percentFree, @timestamp);
            ";

            foreach (var row in rows)
            {
                await using var command = new SqliteCommand(insertSql, connection);
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@machineName", machineName);
                command.Parameters.AddWithValue("@driveLetter", row.DriveLetter ?? string.Empty);
                command.Parameters.AddWithValue("@totalSpaceGb", row.TotalSpaceGb);
                command.Parameters.AddWithValue("@usedSpaceGb", row.UsedSpaceGb);
                command.Parameters.AddWithValue("@freeSpaceGb", row.FreeSpaceGb);
                command.Parameters.AddWithValue("@percentFree", row.PercentFree);
                command.Parameters.AddWithValue("@timestamp", row.Timestamp);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            _logger?.Log($"[DB] Insert committed successfully for {machineNameForLog}");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger?.Log($"[ERROR] Failed to insert RawDriveRows: {ex.Message}");
            throw;
        }
    }
}
