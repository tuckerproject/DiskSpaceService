using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageWatch.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;
using System.Diagnostics;

namespace StorageWatchAgent.Services.AutoUpdate;

/// <summary>
/// Startup service that detects pending checkpoints and triggers resume if safe.
/// Resume only occurs if:
/// 1. A checkpoint exists and is structurally valid
/// 2. Multiple signals confirm the update is truly in progress
/// </summary>
public class UnifiedInstallResumeService : IHostedService
{
    private static readonly TimeSpan HandoffInProgressGrace = TimeSpan.FromSeconds(30);

    private readonly IUnifiedInstallCheckpointStore _checkpointStore;
    private readonly IUnifiedInstallCheckpointValidator _checkpointValidator;
    private readonly IUnifiedInstallOrchestrator _orchestrator;
    private readonly IUpdateRestartIntentProcessor _restartIntentProcessor;
    private readonly ILogger<UnifiedInstallResumeService> _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public UnifiedInstallResumeService(
        IUnifiedInstallCheckpointStore checkpointStore,
        IUnifiedInstallCheckpointValidator checkpointValidator,
        IUnifiedInstallOrchestrator orchestrator,
        IUpdateRestartIntentProcessor restartIntentProcessor,
        ILogger<UnifiedInstallResumeService> logger)
    {
        _checkpointStore = checkpointStore;
        _checkpointValidator = checkpointValidator;
        _orchestrator = orchestrator;
        _restartIntentProcessor = restartIntentProcessor;
        _logger = logger;
    }

    public UnifiedInstallResumeService(
        IUnifiedInstallCheckpointStore checkpointStore,
        IUnifiedInstallCheckpointValidator checkpointValidator,
        IUnifiedInstallOrchestrator orchestrator,
        IInstallPathResolver installPathResolver,
        IUserSessionLauncher userSessionLauncher,
        ILogger<UnifiedInstallResumeService> logger)
        : this(
            checkpointStore,
            checkpointValidator,
            orchestrator,
            new UpdateRestartIntentProcessor(
                checkpointStore,
                installPathResolver,
                userSessionLauncher,
                NullLogger<UpdateRestartIntentProcessor>.Instance),
            logger)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loadResult = await _checkpointStore.LoadCheckpointResultAsync(cancellationToken);
            if (!loadResult.Exists)
            {
                _logger.LogDebug("[AUTOUPDATE] No install-plan.json checkpoint found at startup.");
                return;
            }

            if (loadResult.IsCorrupted || loadResult.Checkpoint == null)
            {
                _logger.LogWarning(
                    "[AUTOUPDATE] install-plan.json is corrupted or unreadable; deleting checkpoint and continuing startup. Error={Error}",
                    loadResult.ErrorMessage ?? "<none>");

                try
                {
                    await _checkpointStore.ClearCheckpointAsync(cancellationToken);
                }
                catch (Exception clearEx)
                {
                    _logger.LogWarning(clearEx, "[AUTOUPDATE] Failed to delete corrupted checkpoint during startup cleanup.");
                }

                return;
            }

            var checkpoint = loadResult.Checkpoint;
            _logger.LogInformation(
                "[AUTOUPDATE] Loaded checkpoint for orchestration {OrchestrationId}: IsInstalling={IsInstalling}, CurrentIndex={Index}, Components={ComponentCount}, LastUpdatedAtUtc={LastUpdatedAtUtc}",
                checkpoint.OrchestrationId,
                checkpoint.IsInstalling,
                checkpoint.CurrentComponentIndex,
                checkpoint.Components.Count,
                checkpoint.LastUpdatedAtUtc);

            var hasAgentComponent = checkpoint.Components.Contains("agent", StringComparer.OrdinalIgnoreCase);
            var hasHandoffState = checkpoint.HandoffState != AgentHandoffState.None
                                  || checkpoint.HandoffStartedAtUtc.HasValue
                                  || checkpoint.AgentExitRequestedAtUtc.HasValue
                                  || checkpoint.HandoffCompletedAtUtc.HasValue;

            if (hasAgentComponent && hasHandoffState)
            {
                if (checkpoint.HandoffCompletedAtUtc.HasValue)
                {
                    var pendingRestartIntents = await _restartIntentProcessor.ProcessAsync(checkpoint, cancellationToken);
                    if (pendingRestartIntents)
                    {
                        _logger.LogInformation(
                            "[AUTOUPDATE] Checkpoint {OrchestrationId} has pending restart intent after handoff completion; preserving checkpoint for later retry.",
                            checkpoint.OrchestrationId);
                        return;
                    }

                    _logger.LogInformation(
                        "[AUTOUPDATE] Checkpoint {OrchestrationId} contains handoff-complete marker at {CompletedAt}; clearing checkpoint.",
                        checkpoint.OrchestrationId,
                        checkpoint.HandoffCompletedAtUtc.Value);
                    await DeleteCheckpointSafelyAsync(cancellationToken, "agent handoff completed");
                    return;
                }

                var markerAgeSource = checkpoint.AgentExitRequestedAtUtc
                                      ?? checkpoint.HandoffStartedAtUtc
                                      ?? checkpoint.LastUpdatedAtUtc;
                var markerAge = DateTimeOffset.UtcNow - markerAgeSource;
                var updaterProcessRunning = checkpoint.UpdaterProcessId.HasValue
                                            && IsProcessRunning(checkpoint.UpdaterProcessId.Value);
                var handoffIsInProgress = checkpoint.HandoffState is AgentHandoffState.Started or AgentHandoffState.ExitRequested;
                var handoffIsFresh = markerAge <= HandoffInProgressGrace;

                if (handoffIsInProgress && (updaterProcessRunning || handoffIsFresh))
                {
                    _logger.LogInformation(
                        "[AUTOUPDATE] Checkpoint {OrchestrationId} has in-progress Agent handoff markers (State={State}, AgeSeconds={AgeSeconds:F1}, UpdaterRunning={UpdaterRunning}); preserving checkpoint for updater completion.",
                        checkpoint.OrchestrationId,
                        checkpoint.HandoffState,
                        markerAge.TotalSeconds,
                        updaterProcessRunning);
                    return;
                }

                _logger.LogWarning(
                    "[AUTOUPDATE] Checkpoint {OrchestrationId} has incomplete Agent handoff markers (State={State}, AgeMinutes={AgeMinutes:F1}m, UpdaterRunning={UpdaterRunning}) without handoff-complete marker; treating as failed/stale handoff and clearing checkpoint.",
                    checkpoint.OrchestrationId,
                    checkpoint.HandoffState,
                    markerAge.TotalMinutes,
                    updaterProcessRunning);
                await DeleteCheckpointSafelyAsync(cancellationToken, "incomplete or stale agent handoff");
                return;
            }

            if (!checkpoint.IsInstalling)
            {
                var pendingRestartIntents = await _restartIntentProcessor.ProcessAsync(checkpoint, cancellationToken);
                if (pendingRestartIntents)
                {
                    _logger.LogInformation(
                        "[AUTOUPDATE] Checkpoint {OrchestrationId} is not installing but has pending restart intent; preserving checkpoint.",
                        checkpoint.OrchestrationId);
                    return;
                }

                _logger.LogInformation(
                    "[AUTOUPDATE] Checkpoint {OrchestrationId} is not marked installing; deleting and continuing startup.",
                    checkpoint.OrchestrationId);
                await DeleteCheckpointSafelyAsync(cancellationToken, "not installing");
                return;
            }

            var validation = _checkpointValidator.Validate(checkpoint);
            _logger.LogInformation(
                "[AUTOUPDATE] Resume validation for orchestration {OrchestrationId}: State={State}, Reason={Reason}, Signals={Signals}",
                checkpoint.OrchestrationId,
                validation.State,
                validation.Reason,
                string.Join(" | ", validation.Signals));

            if (validation.ShouldResume)
            {
                _logger.LogWarning(
                    "[AUTOUPDATE] Valid in-progress checkpoint detected for orchestration {OrchestrationId}; resuming update and allowing shutdown only through the validated install path.",
                    checkpoint.OrchestrationId);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _orchestrator.ResumePendingInstallAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[AUTOUPDATE] Resume failed for orchestration {OrchestrationId}", checkpoint.OrchestrationId);
                    }
                }, CancellationToken.None);

                return;
            }

            if (validation.ShouldDelete)
            {
                _logger.LogWarning(
                    "[AUTOUPDATE] Checkpoint {OrchestrationId} resolved to {State}; deleting checkpoint and continuing startup.",
                    checkpoint.OrchestrationId,
                    validation.State);

                await DeleteCheckpointSafelyAsync(cancellationToken, validation.Reason);
                return;
            }

            _logger.LogInformation(
                "[AUTOUPDATE] Checkpoint {OrchestrationId} is pending but not yet eligible for resume; leaving it in place and continuing startup.",
                checkpoint.OrchestrationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AUTOUPDATE] Failed to evaluate pending checkpoint on startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No cleanup needed
        return Task.CompletedTask;
    }

    private async Task DeleteCheckpointSafelyAsync(CancellationToken cancellationToken, string reason)
    {
        try
        {
            await _checkpointStore.ClearCheckpointAsync(cancellationToken);
            _logger.LogInformation("[AUTOUPDATE] Deleted install-plan.json checkpoint ({Reason}).", reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AUTOUPDATE] Failed to delete install-plan.json checkpoint ({Reason}).", reason);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

}
