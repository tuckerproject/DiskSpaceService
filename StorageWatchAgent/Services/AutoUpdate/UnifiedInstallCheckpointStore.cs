using System.Text.Json;
using Microsoft.Extensions.Logging;
using StorageWatchAgent.Services.AutoUpdate.Models;

namespace StorageWatchAgent.Services.AutoUpdate;

/// <summary>
/// Interface for persisting and loading unified install checkpoints.
/// </summary>
public interface IUnifiedInstallCheckpointStore
{
    /// <summary>
    /// Save the current checkpoint state to disk.
    /// </summary>
    Task SaveCheckpointAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the current checkpoint state from disk, or null if none exists.
    /// </summary>
    Task<UnifiedInstallCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the current checkpoint with load metadata so callers can distinguish missing, corrupted, and valid files.
    /// </summary>
    Task<UnifiedInstallCheckpointLoadResult> LoadCheckpointResultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the checkpoint (delete the persisted file).
    /// </summary>
    Task ClearCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether a checkpoint exists.
    /// </summary>
    Task<bool> CheckpointExistsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Disk-backed checkpoint store using JSON serialization.
/// </summary>
public class UnifiedInstallCheckpointStore : IUnifiedInstallCheckpointStore
{
    private const string CheckpointFileName = "install-plan.json";
    private static readonly string CheckpointDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StorageWatch",
        "Update"
    );

    private readonly ILogger<UnifiedInstallCheckpointStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UnifiedInstallCheckpointStore(ILogger<UnifiedInstallCheckpointStore> logger)
    {
        _logger = logger;
    }

    private string CheckpointFilePath => Path.Combine(CheckpointDirectory, CheckpointFileName);

    public async Task SaveCheckpointAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            checkpoint.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

            Directory.CreateDirectory(CheckpointDirectory);
            var json = JsonSerializer.Serialize(checkpoint, JsonOptions);

            // Atomic write: write to temp file then replace
            var tempPath = CheckpointFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, CheckpointFilePath, overwrite: true);

            _logger.LogInformation(
                "[AUTOUPDATE-CHECKPOINT] Saved checkpoint. Path={CheckpointPath}, OrchestrationId={OrchestrationId}, Index={Index}/{Total}, Installing={Installing}, HandoffState={HandoffState}, HandoffCompletedAtUtc={HandoffCompletedAtUtc}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                CheckpointFilePath,
                checkpoint.OrchestrationId,
                checkpoint.CurrentComponentIndex,
                checkpoint.Components.Count,
                checkpoint.IsInstalling,
                checkpoint.HandoffState,
                checkpoint.HandoffCompletedAtUtc,
                checkpoint.RestartUIRequested,
                checkpoint.RestartServerRequested
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save checkpoint for orchestration {OrchestrationId}", checkpoint.OrchestrationId);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<UnifiedInstallCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken = default)
    {
        var result = await LoadCheckpointResultAsync(cancellationToken);
        return result.Checkpoint;
    }

    public async Task<UnifiedInstallCheckpointLoadResult> LoadCheckpointResultAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(CheckpointFilePath))
            {
                _logger.LogInformation("[AUTOUPDATE-CHECKPOINT] Checkpoint load found no file. Path={CheckpointPath}", CheckpointFilePath);
                return new UnifiedInstallCheckpointLoadResult
                {
                    Exists = false,
                    IsCorrupted = false,
                    Checkpoint = null,
                    ErrorMessage = null
                };
            }

            try
            {
                var json = await File.ReadAllTextAsync(CheckpointFilePath, cancellationToken);
                var checkpoint = JsonSerializer.Deserialize<UnifiedInstallCheckpoint>(json, JsonOptions);

                if (checkpoint != null)
                {
                    _logger.LogInformation(
                        "[AUTOUPDATE-CHECKPOINT] Loaded valid checkpoint. Path={CheckpointPath}, OrchestrationId={OrchestrationId}, Index={Index}/{Total}, Installing={Installing}, HandoffState={HandoffState}, HandoffStartedAtUtc={HandoffStartedAtUtc}, HandoffCompletedAtUtc={HandoffCompletedAtUtc}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                        CheckpointFilePath,
                        checkpoint.OrchestrationId,
                        checkpoint.CurrentComponentIndex,
                        checkpoint.Components.Count,
                        checkpoint.IsInstalling,
                        checkpoint.HandoffState,
                        checkpoint.HandoffStartedAtUtc,
                        checkpoint.HandoffCompletedAtUtc,
                        checkpoint.RestartUIRequested,
                        checkpoint.RestartServerRequested
                    );

                    return new UnifiedInstallCheckpointLoadResult
                    {
                        Exists = true,
                        IsCorrupted = false,
                        Checkpoint = checkpoint,
                        ErrorMessage = null
                    };
                }

                _logger.LogWarning("[AUTOUPDATE-CHECKPOINT] Checkpoint load classified file as corrupted because deserialization returned null. Path={CheckpointPath}", CheckpointFilePath);
                return new UnifiedInstallCheckpointLoadResult
                {
                    Exists = true,
                    IsCorrupted = true,
                    Checkpoint = null,
                    ErrorMessage = "Checkpoint file deserialized to null."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUTOUPDATE-CHECKPOINT] Checkpoint load classified file as corrupted. Path={CheckpointPath}", CheckpointFilePath);
                return new UnifiedInstallCheckpointLoadResult
                {
                    Exists = true,
                    IsCorrupted = true,
                    Checkpoint = null,
                    ErrorMessage = ex.Message
                };
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearCheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(CheckpointFilePath))
            {
                File.Delete(CheckpointFilePath);
                _logger.LogInformation("[AUTOUPDATE-CHECKPOINT] Cleared checkpoint. Path={CheckpointPath}", CheckpointFilePath);
            }
            else
            {
                _logger.LogDebug("No checkpoint to clear at {Path}", CheckpointFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear checkpoint at {Path}", CheckpointFilePath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> CheckpointExistsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return File.Exists(CheckpointFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
