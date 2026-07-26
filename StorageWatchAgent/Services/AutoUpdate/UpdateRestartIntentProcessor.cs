using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using StorageWatch.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;

namespace StorageWatchAgent.Services.AutoUpdate;

public interface IUpdateRestartIntentProcessor
{
    Task<bool> ProcessAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed class UpdateRestartIntentProcessor : IUpdateRestartIntentProcessor
{
    private static readonly TimeSpan ServerRestartRetryDelay = TimeSpan.FromSeconds(1);
    private const int ServerRestartMaxAttempts = 5;
    private const string ServerServiceName = "StorageWatchServer";

    private readonly IUnifiedInstallCheckpointStore _checkpointStore;
    private readonly IInstallPathResolver _installPathResolver;
    private readonly IUserSessionLauncher _userSessionLauncher;
    private readonly ILogger<UpdateRestartIntentProcessor> _logger;

    public UpdateRestartIntentProcessor(
        IUnifiedInstallCheckpointStore checkpointStore,
        IInstallPathResolver installPathResolver,
        IUserSessionLauncher userSessionLauncher,
        ILogger<UpdateRestartIntentProcessor> logger)
    {
        _checkpointStore = checkpointStore;
        _installPathResolver = installPathResolver;
        _userSessionLauncher = userSessionLauncher;
        _logger = logger;
    }

    public async Task<bool> ProcessAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var restartUiRequested = checkpoint.RestartUIRequested;
        var restartServerRequested = checkpoint.RestartServerRequested;
        _logger.LogInformation("[AUTOUPDATE-RESTART] Processor entered. OrchestrationId={OrchestrationId}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}, CancellationRequested={CancellationRequested}",
            checkpoint.OrchestrationId,
            restartUiRequested,
            restartServerRequested,
            cancellationToken.IsCancellationRequested);
        if (!restartUiRequested && !restartServerRequested)
        {
            _logger.LogInformation("[AUTOUPDATE-RESTART] No restart intent is pending; processor is exiting. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
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
            _logger.LogInformation("[AUTOUPDATE-RESTART] Server restart attempt starting. OrchestrationId={OrchestrationId}, ServiceName={ServiceName}", checkpoint.OrchestrationId, ServerServiceName);
            var serverRestarted = await TryStartServerServiceAsync(ServerServiceName, cancellationToken);
            _logger.LogInformation("[AUTOUPDATE-RESTART] Server restart attempt completed. OrchestrationId={OrchestrationId}, ServiceName={ServiceName}, Succeeded={Succeeded}", checkpoint.OrchestrationId, ServerServiceName, serverRestarted);
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
            try
            {
                var resolvedPaths = _installPathResolver.Resolve();
                var uiExecutablePath = Path.Combine(resolvedPaths.UiDirectory, "StorageWatchUI.exe");
                _logger.LogInformation("[AUTOUPDATE-RESTART] UI restart attempt starting. OrchestrationId={OrchestrationId}, ExecutablePath={ExecutablePath}", checkpoint.OrchestrationId, uiExecutablePath);
                var restarted = _userSessionLauncher.TryRestartUI(uiExecutablePath, out var sessionId);
                _logger.LogInformation("[AUTOUPDATE-RESTART] UI restart attempt completed. OrchestrationId={OrchestrationId}, Succeeded={Succeeded}, SessionId={SessionId}", checkpoint.OrchestrationId, restarted, sessionId);
                if (restarted)
                {
                    checkpoint.RestartUIRequested = false;
                    checkpointUpdated = true;
                    _logger.LogInformation("[UI-RESTART] Restart intent completed in session {SessionId}.", sessionId.HasValue ? sessionId.Value : -1);
                }
                else
                {
                    _logger.LogWarning("[UI-RESTART] Restart intent remains pending because no interactive user-session launch succeeded. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UI-RESTART] UI restart attempt failed; retaining intent for retry. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
            }
        }

        if (checkpointUpdated)
        {
            try
            {
                _logger.LogInformation("[AUTOUPDATE-RESTART] Persisting cleared restart intent flags. OrchestrationId={OrchestrationId}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}", checkpoint.OrchestrationId, checkpoint.RestartUIRequested, checkpoint.RestartServerRequested);
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                _logger.LogInformation("[AUTOUPDATE-RESTART] Cleared restart intent flags were persisted. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
            }
            catch (Exception ex)
            {
                checkpoint.RestartUIRequested = restartUiRequested;
                checkpoint.RestartServerRequested = restartServerRequested;
                _logger.LogError(ex, "[AUTOUPDATE-RESTART] Failed to persist cleared restart intent flags; retaining all original intent for retry. OrchestrationId={OrchestrationId}", checkpoint.OrchestrationId);
            }
        }

        var pendingRestartIntents = checkpoint.RestartUIRequested || checkpoint.RestartServerRequested;
        _logger.LogInformation("[AUTOUPDATE-RESTART] Processor completed. OrchestrationId={OrchestrationId}, PendingRestartIntents={PendingRestartIntents}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}, IntentClearingDecision={IntentClearingDecision}",
            checkpoint.OrchestrationId,
            pendingRestartIntents,
            checkpoint.RestartUIRequested,
            checkpoint.RestartServerRequested,
            pendingRestartIntents ? "retain" : "clear");
        return pendingRestartIntents;
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
                serviceController.Refresh();
                _logger.LogInformation("[SERVER-RESTART] SCM restart attempt {Attempt}/{MaxAttempts}. ServiceName={ServiceName}, Status={Status}", attempt, ServerRestartMaxAttempts, serviceName, serviceController.Status);
                if (serviceController.Status == ServiceControllerStatus.Running)
                {
                    _logger.LogInformation("[SERVER-RESTART] Service {ServiceName} is already running on attempt {Attempt}.", serviceName, attempt);
                    return true;
                }

                if (serviceController.Status == ServiceControllerStatus.Stopped)
                {
                    _logger.LogInformation("[SERVER-RESTART] Starting stopped service {ServiceName} on attempt {Attempt}.", serviceName, attempt);
                    serviceController.Start();
                }

                await Task.Delay(ServerRestartRetryDelay, cancellationToken);
            }

            serviceController.Refresh();
            var started = serviceController.Status == ServiceControllerStatus.Running;
            _logger.LogInformation("[SERVER-RESTART] SCM restart attempts exhausted. ServiceName={ServiceName}, FinalStatus={Status}, Succeeded={Succeeded}", serviceName, serviceController.Status, started);
            return started;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SERVER-RESTART] Failed to start service {ServiceName} from restart intent.", serviceName);
            return false;
        }
    }
}
