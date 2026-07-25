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
                serviceController.Refresh();
                if (serviceController.Status == ServiceControllerStatus.Running)
                {
                    return true;
                }

                if (serviceController.Status == ServiceControllerStatus.Stopped)
                {
                    serviceController.Start();
                }

                await Task.Delay(ServerRestartRetryDelay, cancellationToken);
            }

            serviceController.Refresh();
            return serviceController.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SERVER-RESTART] Failed to start service {ServiceName} from restart intent.", serviceName);
            return false;
        }
    }
}
