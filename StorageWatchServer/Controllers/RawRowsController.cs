using Microsoft.AspNetCore.Mvc;
using StorageWatchServer.Server.Api;
using StorageWatchServer.Server.Models;
using StorageWatchServer.Server.Reporting;
using StorageWatchServer.Services.Logging;

namespace StorageWatchServer.Controllers;

/// <summary>
/// API controller for raw row ingestion from Agents.
/// Accepts batch ingestion without any transformation or normalization.
/// </summary>
[ApiController]
[Route("api/agent")]
public class RawRowsController : ControllerBase
{
    private readonly RawRowIngestionService _ingestionService;
    private readonly ILogger<RawRowsController> _logger;
    private readonly RollingFileLogger? _rollingLogger;

    public RawRowsController(RawRowIngestionService ingestionService, ILogger<RawRowsController> logger, RollingFileLogger? rollingLogger = null)
    {
        _ingestionService = ingestionService;
        _logger = logger;
        _rollingLogger = rollingLogger;
    }

    /// <summary>
    /// POST /api/agent/report
    /// Accepts a batch of raw drive rows from an Agent.
    /// </summary>
    [HttpPost("report")]
    public async Task<IActionResult> PostReport([FromBody] AgentReportRequest request)
    {
        // Validate input
        if (request == null)
        {
            _rollingLogger?.Log("[WEBHOST] Endpoint /api/agent/report failed: Request body is required");
            return BadRequest(new { error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.MachineName))
        {
            _rollingLogger?.Log("[WEBHOST] Endpoint /api/agent/report failed: machineName is required");
            return BadRequest(new { error = "machineName is required." });
        }

        if (request.Rows == null)
        {
            _rollingLogger?.Log("[WEBHOST] Endpoint /api/agent/report failed: rows must be a non-null array");
            return BadRequest(new { error = "rows must be a non-null array." });
        }

        if (request.Rows.Count == 0)
        {
            _rollingLogger?.Log("[WEBHOST] Endpoint /api/agent/report failed: rows array must not be empty");
            return BadRequest(new { error = "rows array must not be empty." });
        }

        // Validate each row
        foreach (var row in request.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.DriveLetter))
            {
                _rollingLogger?.Log("[WEBHOST] Endpoint /api/agent/report failed: All rows must have a driveLetter");
                return BadRequest(new { error = "All rows must have a driveLetter." });
            }
        }

        var machineNameForLog = request.MachineName
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

        try
        {
            // Convert request rows to domain model
            var domainRows = request.Rows.Select(r => new RawDriveRow
            {
                DriveLetter = r.DriveLetter,
                TotalSpaceGb = r.TotalSpaceGb,
                UsedSpaceGb = r.UsedSpaceGb,
                FreeSpaceGb = r.FreeSpaceGb,
                PercentFree = r.PercentFree,
                Timestamp = r.Timestamp
            }).ToList();

            // Insert into database
            await _ingestionService.IngestRawRowsAsync(request.MachineName, domainRows);

            _logger.LogInformation(
                "Batch report accepted from machine '{MachineName}' with {RowCount} rows.",
                machineNameForLog,
                request.Rows.Count);

            return Ok(new { message = "Batch report received and processed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing batch report from machine '{MachineName}'", machineNameForLog);
            _rollingLogger?.Log($"[ERROR] Endpoint /api/agent/report failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "An error occurred processing the report." });
        }
    }
}
