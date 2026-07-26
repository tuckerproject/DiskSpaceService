using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        _logger.LogInformation("[AUTOUPDATE-RESUME] UnifiedInstallResumeService constructor completed. CheckpointStore={CheckpointStoreType}, Validator={ValidatorType}, Orchestrator={OrchestratorType}, RestartProcessor={RestartProcessorType}",
            _checkpointStore.GetType().Name,
            _checkpointValidator.GetType().Name,
            _orchestrator.GetType().Name,
            _restartIntentProcessor.GetType().Name);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[AUTOUPDATE-RESUME] StartAsync entered. CancellationRequested={CancellationRequested}", cancellationToken.IsCancellationRequested);
        try
        {
            _logger.LogInformation("[AUTOUPDATE-RESUME] Loading startup checkpoint.");
            var loadResult = await _checkpointStore.LoadCheckpointResultAsync(cancellationToken);
            _logger.LogInformation("[AUTOUPDATE-RESUME] Startup checkpoint load completed. Exists={Exists}, IsCorrupted={IsCorrupted}, HasCheckpoint={HasCheckpoint}, Error={Error}",
                loadResult.Exists,
                loadResult.IsCorrupted,
                loadResult.Checkpoint != null,
                loadResult.ErrorMessage ?? "<none>");
            if (!loadResult.Exists)
            {
                _logger.LogInformation("[AUTOUPDATE-RESUME] No install-plan.json checkpoint found at startup; no resume or restart intent processing is required.");
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
                "[AUTOUPDATE-RESUME] Loaded checkpoint for orchestration {OrchestrationId}: IsInstalling={IsInstalling}, CurrentIndex={Index}, Components={ComponentCount}, LastUpdatedAtUtc={LastUpdatedAtUtc}, HandoffState={HandoffState}, HandoffStartedAtUtc={HandoffStartedAtUtc}, AgentExitRequestedAtUtc={AgentExitRequestedAtUtc}, HandoffCompletedAtUtc={HandoffCompletedAtUtc}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}, UpdaterProcessId={UpdaterProcessId}",
                checkpoint.OrchestrationId,
                checkpoint.IsInstalling,
                checkpoint.CurrentComponentIndex,
                checkpoint.Components.Count,
                checkpoint.LastUpdatedAtUtc,
                checkpoint.HandoffState,
                checkpoint.HandoffStartedAtUtc,
                checkpoint.AgentExitRequestedAtUtc,
                checkpoint.HandoffCompletedAtUtc,
                checkpoint.RestartUIRequested,
                checkpoint.RestartServerRequested,
                checkpoint.UpdaterProcessId);

            var hasAgentComponent = checkpoint.Components.Contains("agent", StringComparer.OrdinalIgnoreCase);
            var hasHandoffState = checkpoint.HandoffState != AgentHandoffState.None
                                  || checkpoint.HandoffStartedAtUtc.HasValue
                                  || checkpoint.AgentExitRequestedAtUtc.HasValue
                                  || checkpoint.HandoffCompletedAtUtc.HasValue;

            var hasRestartIntent = checkpoint.RestartUIRequested || checkpoint.RestartServerRequested;
            if (checkpoint.HandoffCompletedAtUtc.HasValue && hasRestartIntent)
            {
                if (!hasAgentComponent || !checkpoint.HandoffStartedAtUtc.HasValue)
                {
                    _logger.LogWarning("[AUTOUPDATE-RESUME] Completed handoff checkpoint has incomplete diagnostic markers, but restart intent will still be processed immediately. OrchestrationId={OrchestrationId}, HasAgentComponent={HasAgentComponent}, HandoffStartedAtUtc={HandoffStartedAtUtc}, HandoffState={HandoffState}",
                        checkpoint.OrchestrationId,
                        hasAgentComponent,
                        checkpoint.HandoffStartedAtUtc,
                        checkpoint.HandoffState);
                }

                _logger.LogInformation("[AUTOUPDATE-RESUME] Completed handoff with restart intent takes priority over checkpoint validation and safety checks. OrchestrationId={OrchestrationId}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                    checkpoint.OrchestrationId,
                    checkpoint.RestartUIRequested,
                    checkpoint.RestartServerRequested);

                var pendingRestartIntents = await _restartIntentProcessor.ProcessAsync(checkpoint, cancellationToken);
                _logger.LogInformation("[AUTOUPDATE-RESUME] Completed-handoff restart processing finished. OrchestrationId={OrchestrationId}, PendingRestartIntents={PendingRestartIntents}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                    checkpoint.OrchestrationId,
                    pendingRestartIntents,
                    checkpoint.RestartUIRequested,
                    checkpoint.RestartServerRequested);

                if (pendingRestartIntents)
                {
                    _logger.LogWarning("[AUTOUPDATE-RESUME] Restart intent remains after completed Agent handoff; retaining checkpoint for retry. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
                    return;
                }

                await DeleteCheckpointSafelyAsync(cancellationToken, "completed Agent handoff restart intent processed");
                return;
            }

            if (hasAgentComponent && hasHandoffState)
            {
                _logger.LogInformation("[AUTOUPDATE-RESUME] Agent handoff checkpoint detected. HasAgentComponent={HasAgentComponent}, HasHandoffState={HasHandoffState}, HandoffCompleted={HandoffCompleted}",
                    hasAgentComponent,
                    hasHandoffState,
                    checkpoint.HandoffCompletedAtUtc.HasValue);
                if (checkpoint.HandoffCompletedAtUtc.HasValue)
                {
                    _logger.LogInformation("[AUTOUPDATE-RESUME] Entering restart-intent processor for completed Agent handoff. OrchestrationId={OrchestrationId}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                        checkpoint.OrchestrationId,
                        checkpoint.RestartUIRequested,
                        checkpoint.RestartServerRequested);
                    var pendingRestartIntents = await _restartIntentProcessor.ProcessAsync(checkpoint, cancellationToken);
                    _logger.LogInformation("[AUTOUPDATE-RESUME] Restart-intent processor completed for completed Agent handoff. OrchestrationId={OrchestrationId}, PendingRestartIntents={PendingRestartIntents}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                        checkpoint.OrchestrationId,
                        pendingRestartIntents,
                        checkpoint.RestartUIRequested,
                        checkpoint.RestartServerRequested);
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

                _logger.LogInformation("[AUTOUPDATE-RESUME] Evaluated incomplete Agent handoff safety. OrchestrationId={OrchestrationId}, State={State}, MarkerAgeSeconds={MarkerAgeSeconds:F1}, UpdaterRunning={UpdaterRunning}, HandoffIsInProgress={HandoffIsInProgress}, HandoffIsFresh={HandoffIsFresh}",
                    checkpoint.OrchestrationId,
                    checkpoint.HandoffState,
                    markerAge.TotalSeconds,
                    updaterProcessRunning,
                    handoffIsInProgress,
                    handoffIsFresh);

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
                _logger.LogInformation("[AUTOUPDATE-RESUME] Checkpoint is not installing; entering restart-intent processor. OrchestrationId={OrchestrationId}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                    checkpoint.OrchestrationId,
                    checkpoint.RestartUIRequested,
                    checkpoint.RestartServerRequested);
                var pendingRestartIntents = await _restartIntentProcessor.ProcessAsync(checkpoint, cancellationToken);
                _logger.LogInformation("[AUTOUPDATE-RESUME] Restart-intent processor completed for non-installing checkpoint. OrchestrationId={OrchestrationId}, PendingRestartIntents={PendingRestartIntents}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
                    checkpoint.OrchestrationId,
                    pendingRestartIntents,
                    checkpoint.RestartUIRequested,
                    checkpoint.RestartServerRequested);
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
                "[AUTOUPDATE-RESUME] Resume validation completed for orchestration {OrchestrationId}: State={State}, ShouldResume={ShouldResume}, ShouldDelete={ShouldDelete}, IsCorrupted={IsCorrupted}, Reason={Reason}, Signals={Signals}",
                checkpoint.OrchestrationId,
                validation.State,
                validation.ShouldResume,
                validation.ShouldDelete,
                validation.IsCorrupted,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[AUTOUPDATE-RESUME] StartAsync was cancelled while evaluating the startup checkpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AUTOUPDATE-RESUME] Failed to evaluate pending checkpoint on startup.");
        }
        finally
        {
            _logger.LogInformation("[AUTOUPDATE-RESUME] StartAsync completed.");
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
