using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StorageWatch.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;
using System.Diagnostics;
using System.ServiceProcess;

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
    private static readonly TimeSpan ServerRestartRetryDelay = TimeSpan.FromSeconds(1);
    private const int ServerRestartMaxAttempts = 5;
    private const string ServerServiceName = "StorageWatchServer";

    private readonly IUnifiedInstallCheckpointStore _checkpointStore;
    private readonly IUnifiedInstallCheckpointValidator _checkpointValidator;
    private readonly IUnifiedInstallOrchestrator _orchestrator;
    private readonly IInstallPathResolver _installPathResolver;
    private readonly IUserSessionLauncher _userSessionLauncher;
    private readonly ILogger<UnifiedInstallResumeService> _logger;

    public UnifiedInstallResumeService(
        IUnifiedInstallCheckpointStore checkpointStore,
        IUnifiedInstallCheckpointValidator checkpointValidator,
        IUnifiedInstallOrchestrator orchestrator,
        IInstallPathResolver installPathResolver,
        IUserSessionLauncher userSessionLauncher,
        ILogger<UnifiedInstallResumeService> logger)
    {
        _checkpointStore = checkpointStore;
        _checkpointValidator = checkpointValidator;
        _orchestrator = orchestrator;
        _installPathResolver = installPathResolver;
        _userSessionLauncher = userSessionLauncher;
        _logger = logger;
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
                    var pendingRestartIntents = await ProcessRestartIntentsAsync(checkpoint, cancellationToken);
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
                var pendingRestartIntents = await ProcessRestartIntentsAsync(checkpoint, cancellationToken);
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

    private async Task<bool> ProcessRestartIntentsAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var restartUiRequested = checkpoint.RestartUIRequested;
        var restartServerRequested = checkpoint.RestartServerRequested;
        if (!restartUiRequested && !restartServerRequested)
        {
            return false;
        }

        _logger.LogInformation(
            "[AUTOUPDATE] Processing restart intent from checkpoint {OrchestrationId}: RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}",
            checkpoint.OrchestrationId,
            restartUiRequested,
            restartServerRequested);

        var checkpointUpdated = false;

        if (restartServerRequested)
        {
            var serverRestarted = await TryStartServerServiceAsync(ServerServiceName, cancellationToken);
            if (serverRestarted)
            {
                checkpoint.RestartServerRequested = false;
                checkpointUpdated = true;
                _logger.LogInformation("[SERVER-RESTART] Restart intent completed via SCM for service {ServiceName}.", ServerServiceName);
            }
            else
            {
                _logger.LogWarning("[SERVER-RESTART] Restart intent remains pending after SCM start attempts for service {ServiceName}.", ServerServiceName);
            }
        }

        if (restartUiRequested)
        {
            var resolvedPaths = _installPathResolver.Resolve();
            var uiExecutablePath = Path.Combine(resolvedPaths.UiDirectory, "StorageWatchUI.exe");
            var restarted = _userSessionLauncher.TryRestartUI(uiExecutablePath, out var sessionId);
            if (restarted)
            {
                checkpoint.RestartUIRequested = false;
                checkpointUpdated = true;
                _logger.LogInformation("[UI-RESTART] Restart intent completed in session {SessionId}.", sessionId.HasValue ? sessionId.Value : -1);
            }
            else
            {
                _logger.LogInformation("[UI-RESTART] Restart intent remains pending after user-session launch attempt.");
            }
        }

        if (checkpointUpdated)
        {
            await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
        }

        return checkpoint.RestartUIRequested || checkpoint.RestartServerRequested;
    }

    private async Task<bool> TryStartServerServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("[SERVER-RESTART] SCM restart skipped because OS is not Windows.");
            return false;
        }

        try
        {
            using var serviceController = new ServiceController(serviceName);
            _logger.LogInformation("[SERVER-RESTART] Service status before restart attempt: {Status}", serviceController.Status);

            for (var attempt = 1; attempt <= ServerRestartMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    serviceController.Refresh();
                    var status = serviceController.Status;
                    _logger.LogInformation("[SERVER-RESTART] SCM start attempt {Attempt}/{MaxAttempts}. CurrentStatus={Status}", attempt, ServerRestartMaxAttempts, status);

                    if (status == ServiceControllerStatus.Running)
                    {
                        _logger.LogInformation("[SERVER-RESTART] Service already running.");
                        return true;
                    }

                    if (status == ServiceControllerStatus.StopPending)
                    {
                        serviceController.WaitForStatus(ServiceControllerStatus.Stopped, ServerRestartRetryDelay);
                        serviceController.Refresh();
                    }

                    if (serviceController.Status == ServiceControllerStatus.Stopped)
                    {
                        serviceController.Start();
                    }

                    serviceController.WaitForStatus(ServiceControllerStatus.Running, ServerRestartRetryDelay);
                    serviceController.Refresh();
                    if (serviceController.Status == ServiceControllerStatus.Running)
                    {
                        _logger.LogInformation("[SERVER-RESTART] Service reached Running state.");
                        return true;
                    }
                }
                catch (Exception ex) when (attempt < ServerRestartMaxAttempts)
                {
                    _logger.LogWarning(ex, "[SERVER-RESTART] SCM attempt {Attempt} failed; retrying.", attempt);
                }

                await Task.Delay(ServerRestartRetryDelay, cancellationToken);
            }

            serviceController.Refresh();
            _logger.LogWarning("[SERVER-RESTART] SCM restart failed after retries. FinalStatus={Status}", serviceController.Status);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SERVER-RESTART] SCM restart threw exception.");
            return false;
        }
    }
}
