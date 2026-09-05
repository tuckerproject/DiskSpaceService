using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StorageWatchServer.Server.Api;
using StorageWatchServer.Server.Data;
using StorageWatchServer.Server.Services;
using StorageWatchServer.Tests.Utilities;
using Xunit;

namespace StorageWatchServer.Tests.Api;

public class ApiEndpointsIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private readonly string _testDatabaseId = Guid.NewGuid().ToString("N")[..8];
    private ServerSchema? _schema;

    public async Task InitializeAsync()
    {
        // Set test environment flag before building the factory
        AppContext.SetData("IsTestEnvironment", true);
        
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Exclude configuration file loading in tests
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "Server:ListenUrl", "http://localhost:5001" },
                        { "Server:DatabasePath", $"file:memdb_api_{_testDatabaseId}?mode=memory&cache=shared" },
                        { "Server:OnlineTimeoutMinutes", "5" }
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Configure test-specific services with unique in-memory database
                    var serverOptions = new ServerOptions
                    {
                        ListenUrl = "http://localhost:5001",
                        DatabasePath = $"file:memdb_api_{_testDatabaseId}?mode=memory&cache=shared",
                        OnlineTimeoutMinutes = 5
                    };

                    // Remove existing ServerOptions and add test version
                    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ServerOptions));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddSingleton(serverOptions);

                    // Remove and re-register ServerSchema
                    var schemaDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ServerSchema));
                    if (schemaDescriptor != null)
                    {
                        services.Remove(schemaDescriptor);
                    }
                    services.AddSingleton<ServerSchema>();
                });
            });

        _client = _factory.CreateClient();

        // Initialize database for the test
        _schema = _factory.Services.GetRequiredService<ServerSchema>();
        await _schema.InitializeDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ReportEndpoint_AcceptsValidPayload_Returns200Ok()
    {
        // Arrange
        var request = new AgentReportRequest
        {
            MachineName = "TestMachine",
            Rows = new List<RawDriveRowRequest>
            {
                new()
                {
                    DriveLetter = "C:",
                    TotalSpaceGb = 500,
                    UsedSpaceGb = 250,
                    FreeSpaceGb = 250,
                    PercentFree = 50,
                    Timestamp = DateTime.UtcNow
                },
                new()
                {
                    DriveLetter = "D:",
                    TotalSpaceGb = 1000,
                    UsedSpaceGb = 500,
                    FreeSpaceGb = 500,
                    PercentFree = 50,
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var response = await _client!.PostAsJsonAsync("/api/agent/report", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("Batch report received and processed", responseContent);
    }

    [Fact]
    public async Task ReportEndpoint_RejectsMissingMachineName_Returns400BadRequest()
    {
        // Arrange
        var request = new AgentReportRequest
        {
            MachineName = "", // Missing machine name
            Rows = new List<RawDriveRowRequest>
            {
                new()
                {
                    DriveLetter = "C:",
                    TotalSpaceGb = 500,
                    UsedSpaceGb = 250,
                    FreeSpaceGb = 250,
                    PercentFree = 50,
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var response = await _client!.PostAsJsonAsync("/api/agent/report", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("machineName is required", responseContent);
    }

    [Fact]
    public async Task ReportEndpoint_RejectsMachineNameWithLineBreaks_Returns400BadRequest()
    {
        // Arrange
        var request = new AgentReportRequest
        {
            MachineName = "TestMachine\r\nForgedEntry",
            Rows = new List<RawDriveRowRequest>
            {
                new()
                {
                    DriveLetter = "C:",
                    TotalSpaceGb = 500,
                    UsedSpaceGb = 250,
                    FreeSpaceGb = 250,
                    PercentFree = 50,
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var response = await _client!.PostAsJsonAsync("/api/agent/report", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("machineName contains invalid line break characters", responseContent);
    }

    [Fact]
    public async Task ReportEndpoint_RejectsNullRows_Returns400BadRequest()
    {
        // Arrange
        var json = """{"machineName":"TestMachine","rows":null}""";
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _client!.PostAsync("/api/agent/report", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReportEndpoint_RejectsEmptyRowsArray_Returns400BadRequest()
    {
        // Arrange
        var request = new AgentReportRequest
        {
            MachineName = "TestMachine",
            Rows = new List<RawDriveRowRequest>() // Empty array
        };

        // Act
        var response = await _client!.PostAsJsonAsync("/api/agent/report", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("rows array must not be empty", responseContent);
    }

    [Fact]
    public async Task ReportEndpoint_RejectsMissingDriveLetter_Returns400BadRequest()
    {
        // Arrange
        var request = new AgentReportRequest
        {
            MachineName = "TestMachine",
            Rows = new List<RawDriveRowRequest>
            {
                new()
                {
                    DriveLetter = "", // Missing drive letter
                    TotalSpaceGb = 500,
                    UsedSpaceGb = 250,
                    FreeSpaceGb = 250,
                    PercentFree = 50,
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var response = await _client!.PostAsJsonAsync("/api/agent/report", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("driveLetter", responseContent);
    }
}
