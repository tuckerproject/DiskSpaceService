using Microsoft.Data.Sqlite;
using StorageWatchServer.Server.Models;
using StorageWatchServer.Server.Reporting;
using StorageWatchServer.Tests.Utilities;
using Xunit;

namespace StorageWatchServer.Tests.Services;

/// <summary>
/// Unit tests for the RawRowIngestionService.
/// Validates that rows are inserted exactly as received with no transformations.
/// </summary>
public class RawRowIngestionServiceTests : IAsyncLifetime
{
    private TestDatabaseFactory? _factory;
    private RawRowIngestionService? _ingestionService;

    public async Task InitializeAsync()
    {
        _factory = await TestDatabaseFactory.CreateAsync();
        _ingestionService = _factory.GetIngestionService();
    }

    public async Task DisposeAsync()
    {
        await (_factory?.DisposeAsync() ?? ValueTask.CompletedTask);
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithValidBatch_InsertsRowsAsIs()
    {
        // Arrange
        var machineName = "TestMachine";
        var now = DateTime.UtcNow;
        var rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500.5,
                UsedSpaceGb = 250.25,
                FreeSpaceGb = 250.25,
                PercentFree = 50.05,
                Timestamp = now
            },
            new()
            {
                MachineName = machineName,
                DriveLetter = "D:",
                TotalSpaceGb = 1000.75,
                UsedSpaceGb = 600.5,
                FreeSpaceGb = 400.25,
                PercentFree = 40.025,
                Timestamp = now
            }
        };

        // Act
        await _ingestionService!.IngestRawRowsAsync(machineName, rows);

        // Assert - verify rows were inserted exactly as provided
        var serverOptions = _factory!.GetOptions();
        await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
        await connection.OpenAsync();

        const string query = @"
            SELECT DriveLetter, TotalSpaceGb, UsedSpaceGb, FreeSpaceGb, PercentFree
            FROM RawDriveRows
            WHERE MachineName = @machineName
            ORDER BY DriveLetter";

        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("@machineName", machineName);

        await using var reader = await command.ExecuteReaderAsync();

        // Read first row (C:)
        Assert.True(await reader.ReadAsync());
        Assert.Equal("C:", reader.GetString(0));
        Assert.Equal(500.5, reader.GetDouble(1));
        Assert.Equal(250.25, reader.GetDouble(2));
        Assert.Equal(250.25, reader.GetDouble(3));
        Assert.Equal(50.05, reader.GetDouble(4));

        // Read second row (D:)
        Assert.True(await reader.ReadAsync());
        Assert.Equal("D:", reader.GetString(0));
        Assert.Equal(1000.75, reader.GetDouble(1));
        Assert.Equal(600.5, reader.GetDouble(2));
        Assert.Equal(400.25, reader.GetDouble(3));
        Assert.Equal(40.025, reader.GetDouble(4));

        // No more rows
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithEmptyMachineName_ThrowsArgumentException()
    {
        // Arrange
        var rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = "Machine",
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 250,
                FreeSpaceGb = 250,
                PercentFree = 50,
                Timestamp = DateTime.UtcNow
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _ingestionService!.IngestRawRowsAsync("", rows));
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithNullRows_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _ingestionService!.IngestRawRowsAsync("TestMachine", null!));
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithEmptyRowsList_ThrowsArgumentException()
    {
        // Arrange
        var rows = new List<RawDriveRow>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _ingestionService!.IngestRawRowsAsync("TestMachine", rows));
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithCrLfInMachineName_SanitizesMachineNameInLogsOnly()
    {
        // Arrange
        var machineName = "TestMachine\r\nForgedLine";
        var logDirectory = Path.Combine(Path.GetTempPath(), "StorageWatchServerTests", Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(logDirectory, "server.log");
        var logger = new StorageWatchServer.Services.Logging.RollingFileLogger(logPath);
        var ingestionService = new RawRowIngestionService(_factory!.GetOptions(), logger);
        var rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 250,
                FreeSpaceGb = 250,
                PercentFree = 50,
                Timestamp = DateTime.UtcNow
            }
        };

        try
        {
            // Act
            await ingestionService.IngestRawRowsAsync(machineName, rows);

            // Assert
            var logLines = File.ReadAllLines(logPath);
            var logContent = File.ReadAllText(logPath);
            Assert.Equal(3, logLines.Length);
            Assert.DoesNotContain(machineName, logContent, StringComparison.Ordinal);
            Assert.Contains(logLines, line => line.Contains("Received report from TestMachineForgedLine with 1 rows", StringComparison.Ordinal));
            Assert.Contains(logLines, line => line.Contains("Inserting 1 RawDriveRows for TestMachineForgedLine", StringComparison.Ordinal));
            Assert.Contains(logLines, line => line.Contains("Insert committed successfully for TestMachineForgedLine", StringComparison.Ordinal));

            var serverOptions = _factory.GetOptions();
            await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
            await connection.OpenAsync();

            const string query = "SELECT COUNT(*) FROM RawDriveRows WHERE MachineName = @machineName";
            await using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@machineName", machineName);

            var count = (long?)await command.ExecuteScalarAsync();
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithMultipleBatches_InsertsAllRows()
    {
        // Arrange
        var machineName = "TestMachine";
        var now = DateTime.UtcNow;

        var batch1 = new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 250,
                FreeSpaceGb = 250,
                PercentFree = 50,
                Timestamp = now
            }
        };

        var batch2 = new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "D:",
                TotalSpaceGb = 1000,
                UsedSpaceGb = 600,
                FreeSpaceGb = 400,
                PercentFree = 40,
                Timestamp = now.AddSeconds(1)
            }
        };

        // Act
        await _ingestionService!.IngestRawRowsAsync(machineName, batch1);
        await _ingestionService.IngestRawRowsAsync(machineName, batch2);

        // Assert
        var serverOptions = _factory!.GetOptions();
        await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
        await connection.OpenAsync();

        const string countQuery = "SELECT COUNT(*) FROM RawDriveRows WHERE MachineName = @machineName";
        await using var countCommand = new SqliteCommand(countQuery, connection);
        countCommand.Parameters.AddWithValue("@machineName", machineName);
        var count = (long?)await countCommand.ExecuteScalarAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithMultipleMachines_KeepsRowsSeparated()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var machine1Rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = "Machine1",
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 300,
                FreeSpaceGb = 200,
                PercentFree = 40,
                Timestamp = now
            }
        };

        var machine2Rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = "Machine2",
                DriveLetter = "D:",
                TotalSpaceGb = 1000,
                UsedSpaceGb = 200,
                FreeSpaceGb = 800,
                PercentFree = 80,
                Timestamp = now
            }
        };

        // Act
        await _ingestionService!.IngestRawRowsAsync("Machine1", machine1Rows);
        await _ingestionService.IngestRawRowsAsync("Machine2", machine2Rows);

        // Assert
        var serverOptions = _factory!.GetOptions();
        await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
        await connection.OpenAsync();

        const string query = @"
            SELECT MachineName, DriveLetter
            FROM RawDriveRows
            ORDER BY MachineName, DriveLetter";

        await using var command = new SqliteCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Machine1", reader.GetString(0));
        Assert.Equal("C:", reader.GetString(1));

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Machine2", reader.GetString(0));
        Assert.Equal("D:", reader.GetString(1));

        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task IngestRawRowsAsync_WithTimeWindow_QueryCanFilterRecentRows()
    {
        // Arrange
        var machineName = "TimeWindowMachine";
        var oldTimestamp = DateTime.UtcNow.AddDays(-5);
        var recentTimestamp = DateTime.UtcNow;

        await _ingestionService!.IngestRawRowsAsync(machineName, new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 400,
                FreeSpaceGb = 100,
                PercentFree = 20,
                Timestamp = oldTimestamp
            }
        });

        await _ingestionService.IngestRawRowsAsync(machineName, new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 350,
                FreeSpaceGb = 150,
                PercentFree = 30,
                Timestamp = recentTimestamp
            }
        });

        // Act
        var serverOptions = _factory!.GetOptions();
        await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
        await connection.OpenAsync();

        const string query = @"
            SELECT COUNT(*)
            FROM RawDriveRows
            WHERE MachineName = @machineName AND Timestamp >= @startUtc";

        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("@machineName", machineName);
        command.Parameters.AddWithValue("@startUtc", DateTime.UtcNow.AddDays(-1));

        var recentCount = (long?)await command.ExecuteScalarAsync();

        // Assert
        Assert.Equal(1, recentCount);
    }

    [Fact]
    public async Task IngestRawRowsAsync_PreservesTimestamp()
    {
        // Arrange
        var machineName = "TestMachine";
        var specificTime = new DateTime(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc);
        var rows = new List<RawDriveRow>
        {
            new()
            {
                MachineName = machineName,
                DriveLetter = "C:",
                TotalSpaceGb = 500,
                UsedSpaceGb = 250,
                FreeSpaceGb = 250,
                PercentFree = 50,
                Timestamp = specificTime
            }
        };

        // Act
        await _ingestionService!.IngestRawRowsAsync(machineName, rows);

        // Assert
        var serverOptions = _factory!.GetOptions();
        await using var connection = new SqliteConnection($"Data Source={serverOptions.DatabasePath}");
        await connection.OpenAsync();

        const string query = "SELECT Timestamp FROM RawDriveRows WHERE MachineName = @machineName";
        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("@machineName", machineName);

        var result = await command.ExecuteScalarAsync();
        var storedTime = DateTime.Parse((string?)result ?? "");

        // Compare timestamps (allowing for minor precision differences in SQLite)
        Assert.True(Math.Abs((storedTime - specificTime).TotalSeconds) < 1);
    }
}
