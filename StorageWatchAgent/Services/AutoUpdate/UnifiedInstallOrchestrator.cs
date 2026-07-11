using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StorageWatch.Shared.Update.Models;
using StorageWatchAgent.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;

namespace StorageWatch.Services.AutoUpdate;

public interface IUnifiedInstallOrchestrator
{
    Task<UnifiedUpdateInstallResult> StartInstallAsync(UnifiedInstallUpdateRequest request, CancellationToken cancellationToken);
    Task<UnifiedUpdateInstallResult> ResumePendingInstallAsync(CancellationToken cancellationToken);
    UnifiedUpdateProgressInfo GetProgress();
    UnifiedUpdateInstallResult GetLastResult();
}

public class UnifiedInstallOrchestrator : IUnifiedInstallOrchestrator
{
    private static readonly string[] DefaultAllOrder = ["updater", "server", "ui", "agent"];
    private static readonly TimeSpan UpdaterInvocationTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ComponentStopTimeout = TimeSpan.FromSeconds(30);

    private const string UiProcessName = "StorageWatchUI";
    private const string ServerProcessName = "StorageWatchServer";
    private const string AgentProcessName = "StorageWatchAgent";
    private const string ServerServiceName = "StorageWatchServer";
    private const string AgentServiceName = "StorageWatchAgent";

    private readonly IUnifiedUpdateChecker _unifiedUpdateChecker;
    private readonly IServiceUpdateDownloader _serviceUpdateDownloader;
    private readonly IInstallPathResolver _installPathResolver;
    private readonly IUnifiedInstallCheckpointStore _checkpointStore;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<UnifiedInstallOrchestrator> _logger;
    private readonly Func<string, CancellationToken, Task<(bool Success, string? ErrorMessage)>>? _stopComponentBeforeUpdateOverride;
    private readonly Func<string, IReadOnlyList<string>, string, (bool Success, string? ErrorMessage)> _runUpdaterProcess;
    private readonly Func<string, IReadOnlyList<string>, string, (bool Success, int? ProcessId, string? ErrorMessage)> _runUpdaterProcessDetached;
    private readonly Func<string, TimeSpan, (bool Success, string? ErrorMessage)> _stopWindowsService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _orchestrationGate = new(1, 1);
    private readonly object _stateGate = new();
    private UnifiedUpdateProgressInfo _latestProgress = new()
    {
        OrchestrationId = string.Empty,
        Phase = "idle",
        Component = string.Empty,
        Status = "Idle",
        ProgressPercent = 0,
        IsIndeterminate = false
    };
    private UnifiedUpdateInstallResult _lastResult = new()
    {
        OrchestrationId = string.Empty,
        Success = false,
        RestartRequired = false,
        ErrorMessage = null,
        StartedAtUtc = DateTimeOffset.MinValue,
        CompletedAtUtc = DateTimeOffset.MinValue
    };

    private sealed class NoOpHostApplicationLifetime : IHostApplicationLifetime
    {
        public static readonly NoOpHostApplicationLifetime Instance = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    public UnifiedInstallOrchestrator(
        IUnifiedUpdateChecker unifiedUpdateChecker,
        IServiceUpdateDownloader serviceUpdateDownloader,
        IInstallPathResolver installPathResolver,
        IUnifiedInstallCheckpointStore checkpointStore,
        ILogger<UnifiedInstallOrchestrator> logger,
        Func<string, CancellationToken, Task<(bool Success, string? ErrorMessage)>>? stopComponentBeforeUpdate = null,
        Func<string, IReadOnlyList<string>, string, (bool Success, string? ErrorMessage)>? runUpdaterProcess = null,
        Func<string, IReadOnlyList<string>, string, (bool Success, int? ProcessId, string? ErrorMessage)>? runUpdaterProcessDetached = null,
        Func<string, TimeSpan, (bool Success, string? ErrorMessage)>? stopWindowsService = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        : this(
            unifiedUpdateChecker,
            serviceUpdateDownloader,
            installPathResolver,
            checkpointStore,
            NoOpHostApplicationLifetime.Instance,
            logger,
            stopComponentBeforeUpdate,
            runUpdaterProcess,
            runUpdaterProcessDetached,
            stopWindowsService,
            delayAsync)
    {
    }

    public UnifiedInstallOrchestrator(
        IUnifiedUpdateChecker unifiedUpdateChecker,
        IServiceUpdateDownloader serviceUpdateDownloader,
        IInstallPathResolver installPathResolver,
        IUnifiedInstallCheckpointStore checkpointStore,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<UnifiedInstallOrchestrator> logger,
        Func<string, CancellationToken, Task<(bool Success, string? ErrorMessage)>>? stopComponentBeforeUpdate = null,
        Func<string, IReadOnlyList<string>, string, (bool Success, string? ErrorMessage)>? runUpdaterProcess = null,
        Func<string, IReadOnlyList<string>, string, (bool Success, int? ProcessId, string? ErrorMessage)>? runUpdaterProcessDetached = null,
        Func<string, TimeSpan, (bool Success, string? ErrorMessage)>? stopWindowsService = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _unifiedUpdateChecker = unifiedUpdateChecker ?? throw new ArgumentNullException(nameof(unifiedUpdateChecker));
        _serviceUpdateDownloader = serviceUpdateDownloader ?? throw new ArgumentNullException(nameof(serviceUpdateDownloader));
        _installPathResolver = installPathResolver ?? throw new ArgumentNullException(nameof(installPathResolver));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _hostApplicationLifetime = hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stopComponentBeforeUpdateOverride = stopComponentBeforeUpdate;
        _runUpdaterProcess = runUpdaterProcess ?? RunUpdaterProcess;
        _runUpdaterProcessDetached = runUpdaterProcessDetached ?? RunUpdaterProcessDetached;
        _stopWindowsService = stopWindowsService ?? StopWindowsServiceByName;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<UnifiedUpdateInstallResult> StartInstallAsync(UnifiedInstallUpdateRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "[DIAG] StartInstallAsync: UpdateAll={UpdateAll}, RequestedBy={RequestedBy}, Force={Force}, RequestedComponents={RequestedComponents}",
            request.UpdateAll,
            request.RequestedBy,
            request.Force,
            string.Join(", ", request.Components));

        if (!await _orchestrationGate.WaitAsync(0, cancellationToken))
        {
            return new UnifiedUpdateInstallResult
            {
                OrchestrationId = _latestProgress.OrchestrationId,
                Success = false,
                ErrorMessage = "An update orchestration is already active.",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var orchestrationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            SetProgress(orchestrationId, "initializing", string.Empty, "Preparing update orchestration...", 0, true);

            var status = await _unifiedUpdateChecker.RefreshSnapshotAsync(cancellationToken);
            foreach (var c in status.Components)
            {
                _logger.LogDebug(
                    "[DIAG] Snapshot component: {Component}, Current={Current}, Latest={Latest}, UpdateAvailable={Available}",
                    c.Component,
                    c.CurrentVersion,
                    c.LatestVersion,
                    c.UpdateAvailable);
            }

            var plan = BuildPlan(request, status);

            if (plan.Count == 0)
            {
                var noOp = new UnifiedUpdateInstallResult
                {
                    OrchestrationId = orchestrationId,
                    Success = true,
                    RestartRequired = false,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };

                SetProgress(orchestrationId, "completed", string.Empty, "No applicable updates were available.", 100, false);
                SetLastResult(noOp);
                return noOp;
            }

            // Initialize checkpoint before starting install
            var checkpoint = CreateCheckpoint(orchestrationId, startedAt, request, plan, status);
            checkpoint.IsInstalling = true;
            await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
            await _unifiedUpdateChecker.SetInstallingStateAsync(true, cancellationToken);

            _logger.LogInformation(
                "[AUTOUPDATE] Starting unified install for orchestration {OrchestrationId} with {Count} components: {Components}",
                orchestrationId,
                plan.Count,
                string.Join(", ", plan)
            );

            var result = await ExecuteInstallPlanAsync(orchestrationId, startedAt, checkpoint, cancellationToken);

            var handoffActive = checkpoint.HandoffState == AgentHandoffState.ExitRequested
                                && checkpoint.Components.Contains("agent", StringComparer.OrdinalIgnoreCase)
                                && checkpoint.ComponentStates.Any(state =>
                                    string.Equals(state.Component, "agent", StringComparison.OrdinalIgnoreCase)
                                    && state.State == ComponentInstallState.InProgress);

            if (!handoffActive)
            {
                // Clear checkpoint on completion when no detached handoff is pending.
                checkpoint.IsInstalling = false;
                await _checkpointStore.ClearCheckpointAsync(cancellationToken);
                await _unifiedUpdateChecker.SetInstallingStateAsync(false, cancellationToken);
            }

            SetProgress(
                orchestrationId,
                result.Success ? "completed" : "failed",
                string.Empty,
                result.Success ? "Unified update orchestration completed." : (result.ErrorMessage ?? "Unified update orchestration failed."),
                result.Success ? 100 : ComputePercent(result.UpdatedComponents.Count, plan.Count),
                false);
            SetLastResult(result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AUTOUPDATE] Unified install orchestration failed.");

            // Clear checkpoint on fatal error
            try
            {
                await _checkpointStore.ClearCheckpointAsync(cancellationToken);
                await _unifiedUpdateChecker.SetInstallingStateAsync(false, cancellationToken);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(clearEx, "Failed to clear checkpoint after fatal error");
            }

            var failed = new UnifiedUpdateInstallResult
            {
                OrchestrationId = orchestrationId,
                Success = false,
                ErrorMessage = ex.Message,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            SetProgress(orchestrationId, "failed", string.Empty, ex.Message, 0, false);
            SetLastResult(failed);
            return failed;
        }
        finally
        {
            _orchestrationGate.Release();
        }
    }

    public UnifiedUpdateProgressInfo GetProgress()
    {
        lock (_stateGate)
        {
            return Clone(_latestProgress);
        }
    }

    public UnifiedUpdateInstallResult GetLastResult()
    {
        lock (_stateGate)
        {
            return Clone(_lastResult);
        }
    }

    public async Task<UnifiedUpdateInstallResult> ResumePendingInstallAsync(CancellationToken cancellationToken)
    {
        if (!await _orchestrationGate.WaitAsync(0, cancellationToken))
        {
            _logger.LogWarning("[AUTOUPDATE] Resume rejected: orchestration already active");
            return new UnifiedUpdateInstallResult
            {
                OrchestrationId = string.Empty,
                Success = false,
                ErrorMessage = "Cannot resume: an orchestration is already active.",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        try
        {
            var checkpoint = await _checkpointStore.LoadCheckpointAsync(cancellationToken);
            if (checkpoint == null || !checkpoint.IsInstalling)
            {
                _logger.LogInformation("[AUTOUPDATE] No pending checkpoint to resume");
                return new UnifiedUpdateInstallResult
                {
                    OrchestrationId = string.Empty,
                    Success = false,
                    ErrorMessage = "No pending install to resume.",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
            }

            _logger.LogInformation(
                "[AUTOUPDATE] Resuming orchestration {OrchestrationId} from component index {Index}/{Total}",
                checkpoint.OrchestrationId,
                checkpoint.CurrentComponentIndex,
                checkpoint.Components.Count
            );

            SetProgress(checkpoint.OrchestrationId, "resuming", string.Empty, "Resuming update orchestration...", 0, true);

            var result = await ExecuteInstallPlanAsync(
                checkpoint.OrchestrationId,
                checkpoint.StartedAtUtc,
                checkpoint,
                cancellationToken);

            var handoffActive = checkpoint.HandoffState == AgentHandoffState.ExitRequested
                                && checkpoint.Components.Contains("agent", StringComparer.OrdinalIgnoreCase)
                                && checkpoint.ComponentStates.Any(state =>
                                    string.Equals(state.Component, "agent", StringComparison.OrdinalIgnoreCase)
                                    && state.State == ComponentInstallState.InProgress);

            if (!handoffActive)
            {
                // Clear checkpoint after resume completes when detached handoff is not pending.
                checkpoint.IsInstalling = false;
                await _checkpointStore.ClearCheckpointAsync(cancellationToken);
                await _unifiedUpdateChecker.SetInstallingStateAsync(false, cancellationToken);
            }

            SetProgress(
                checkpoint.OrchestrationId,
                result.Success ? "completed" : "failed",
                string.Empty,
                result.Success ? "Unified update orchestration resumed and completed." : (result.ErrorMessage ?? "Resumed orchestration failed."),
                result.Success ? 100 : ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count),
                false);
            SetLastResult(result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AUTOUPDATE] Resume failed");

            try
            {
                await _checkpointStore.ClearCheckpointAsync(cancellationToken);
                await _unifiedUpdateChecker.SetInstallingStateAsync(false, cancellationToken);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(clearEx, "Failed to clear checkpoint after resume error");
            }

            var failed = new UnifiedUpdateInstallResult
            {
                OrchestrationId = string.Empty,
                Success = false,
                ErrorMessage = ex.Message,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            SetLastResult(failed);
            return failed;
        }
        finally
        {
            _orchestrationGate.Release();
        }
    }

    private async Task<UnifiedUpdateInstallResult> ExecuteInstallPlanAsync(
        string orchestrationId,
        DateTimeOffset startedAt,
        UnifiedInstallCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var paths = _installPathResolver.Resolve();
        _logger.LogDebug(
            "[DIAG] Resolved paths. InstallRoot={InstallRoot}, Agent={Agent}, Server={Server}, UI={UI}, Updater={Updater}, UpdaterExe={UpdaterExe}, UsedRegistryInstallRoot={UsedRegistry}",
            paths.InstallRoot,
            paths.AgentDirectory,
            paths.ServerDirectory,
            paths.UiDirectory,
            paths.UpdaterDirectory,
            paths.UpdaterExecutablePath,
            paths.UsedRegistryInstallRoot);

        var result = new UnifiedUpdateInstallResult
        {
            OrchestrationId = orchestrationId,
            StartedAtUtc = startedAt,
            RestartRequired = false
        };

        // Restore already-completed components from checkpoint
        foreach (var state in checkpoint.ComponentStates.Where(s => s.State == ComponentInstallState.Completed))
        {
            result.UpdatedComponents.Add(state.Component);
        }

        for (int i = checkpoint.CurrentComponentIndex; i < checkpoint.Components.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var component = checkpoint.Components[i];
            var componentState = checkpoint.ComponentStates[i];
            _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);

            // Skip if already completed (idempotent resume)
            if (componentState.State == ComponentInstallState.Completed)
            {
                _logger.LogDebug(
                    "[AUTOUPDATE] Component {Component} already completed, skipping (index {Index})",
                    component,
                    i
                );
                continue;
            }

            checkpoint.CurrentComponentIndex = i;
            _logger.LogDebug("[DIAG] Checkpoint index updated: {Index}", checkpoint.CurrentComponentIndex);
            componentState.State = ComponentInstallState.InProgress;
            _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
            componentState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);

            SetProgress(orchestrationId, "installing", component, $"Applying update for {component}...", ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), true);

            _logger.LogInformation(
                "[AUTOUPDATE] Installing component {Component} (index {Index}/{Total}, version {Version})",
                component,
                i,
                checkpoint.Components.Count,
                componentState.TargetVersion
            );

            // Download if not already present
            string? localZipPath = componentState.LocalZipPath;
            if (string.IsNullOrWhiteSpace(localZipPath) || !File.Exists(localZipPath))
            {
                var downloadResult = await _serviceUpdateDownloader.DownloadAsync(new ComponentUpdateInfo
                {
                    Version = componentState.TargetVersion,
                    DownloadUrl = componentState.DownloadUrl,
                    Sha256 = componentState.Sha256
                }, cancellationToken);

                if (!downloadResult.Success || string.IsNullOrWhiteSpace(downloadResult.FilePath))
                {
                    componentState.State = ComponentInstallState.Failed;
                    _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
                    componentState.ErrorMessage = downloadResult.ErrorMessage ?? $"{component} download failed.";
                    result.FailedComponents.Add(component);
                    result.ErrorMessage = componentState.ErrorMessage;
                    await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                    SetProgress(orchestrationId, "failed", component, result.ErrorMessage, ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);
                    break;
                }

                localZipPath = downloadResult.FilePath;
                componentState.LocalZipPath = localZipPath;
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
            }

            // Prepare invocation
            var componentStatus = new UnifiedUpdateComponentStatus
            {
                Component = component,
                LatestVersion = componentState.TargetVersion,
                DownloadUrl = componentState.DownloadUrl,
                Sha256 = componentState.Sha256
            };

            var invocation = PrepareInvocation(component, componentStatus, localZipPath, paths);
            if (!invocation.Success)
            {
                componentState.State = ComponentInstallState.Failed;
                _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
                componentState.ErrorMessage = invocation.Error;
                result.FailedComponents.Add(component);
                result.ErrorMessage = invocation.Error;
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                SetProgress(orchestrationId, "failed", component, result.ErrorMessage ?? $"Failed to prepare update for {component}.", ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);
                break;
            }

            var invocationSource = invocation.Arguments
                .SkipWhile(arg => !string.Equals(arg, "--source", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var invocationTarget = invocation.Arguments
                .SkipWhile(arg => !string.Equals(arg, "--target", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var invocationManifest = invocation.Arguments
                .SkipWhile(arg => !string.Equals(arg, "--manifest", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            _logger.LogDebug(
                "[DIAG] PrepareInvocation: Component={Component}, UpdaterPath={Updater}, Source={Source}, Target={Target}, Manifest={Manifest}",
                component,
                invocation.UpdaterPath,
                invocationSource,
                invocationTarget,
                invocationManifest);
            _logger.LogDebug("[DIAG] Scheduling updater args: {Args}", string.Join(" ", invocation.Arguments));
            try
            {
                var fviUpdater = FileVersionInfo.GetVersionInfo(invocation.UpdaterPath);
                _logger.LogDebug("[DIAG] Updater version at invocation: {Version}", fviUpdater.FileVersion);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DIAG] Failed to read updater version at invocation from {Path}", invocation.UpdaterPath);
            }

            var isAgentComponent = string.Equals(component, "agent", StringComparison.OrdinalIgnoreCase);
            if (!isAgentComponent)
            {
                var stop = await StopComponentBeforeUpdateAsync(component, cancellationToken);
                if (!stop.Success)
                {
                    componentState.State = ComponentInstallState.Failed;
                    _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
                    componentState.ErrorMessage = stop.ErrorMessage ?? $"Failed to stop component '{component}' before update.";
                    result.FailedComponents.Add(component);
                    result.ErrorMessage = componentState.ErrorMessage;
                    await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                    SetProgress(orchestrationId, "failed", component, result.ErrorMessage, ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);
                    break;
                }
            }

            if (isAgentComponent)
            {
                checkpoint.HandoffStartedAtUtc = DateTimeOffset.UtcNow;
                checkpoint.HandoffState = AgentHandoffState.Started;
                checkpoint.ResumeAttemptCount = 0;
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);

                var detached = _runUpdaterProcessDetached(invocation.UpdaterPath, invocation.Arguments, invocation.WorkingDirectory);
                if (!detached.Success)
                {
                    componentState.State = ComponentInstallState.Failed;
                    componentState.ErrorMessage = detached.ErrorMessage ?? "Failed to start detached updater process.";
                    checkpoint.HandoffState = AgentHandoffState.Failed;
                    result.FailedComponents.Add(component);
                    result.ErrorMessage = componentState.ErrorMessage;
                    await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                    SetProgress(orchestrationId, "failed", component, result.ErrorMessage, ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);
                    break;
                }

                checkpoint.UpdaterProcessId = detached.ProcessId;
                checkpoint.AgentExitRequestedAtUtc = DateTimeOffset.UtcNow;
                checkpoint.HandoffState = AgentHandoffState.ExitRequested;
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);

                SetProgress(orchestrationId, "handoff", component, "Updater handoff started. Awaiting updater-owned handoff completion marker before Agent restart...", ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), true);
                result.Success = true;
                result.RestartRequired = true;
                result.CompletedAtUtc = DateTimeOffset.UtcNow;
                _logger.LogInformation("[AUTOUPDATE] Agent handoff launched updater process {ProcessId}; requesting host shutdown. Updater is responsible for persisting handoff completion marker.", detached.ProcessId);
                _hostApplicationLifetime.StopApplication();
                return result;
            }

            // Run updater
            var run = _runUpdaterProcess(invocation.UpdaterPath, invocation.Arguments, invocation.WorkingDirectory);
            if (!run.Success)
            {
                componentState.State = ComponentInstallState.Failed;
                _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
                componentState.ErrorMessage = run.ErrorMessage;
                result.FailedComponents.Add(component);
                result.ErrorMessage = run.ErrorMessage ?? $"Failed to launch updater for component '{component}'.";
                await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
                SetProgress(orchestrationId, "failed", component, result.ErrorMessage, ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);
                break;
            }

            // Mark completed
            componentState.State = ComponentInstallState.Completed;
            _logger.LogDebug("[DIAG] Component execution state: Component={Component}, State={State}", component, componentState.State);
            componentState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            result.UpdatedComponents.Add(component);
            await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
            SetProgress(orchestrationId, "installing", component, $"Update applied for {component}.", ComputePercent(result.UpdatedComponents.Count, checkpoint.Components.Count), false);

            _logger.LogInformation(
                "[AUTOUPDATE] Component {Component} completed successfully (index {Index})",
                component,
                i
            );
        }

        result.Success = result.FailedComponents.Count == 0;
        result.CompletedAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    private static UnifiedInstallCheckpoint CreateCheckpoint(
        string orchestrationId,
        DateTimeOffset startedAt,
        UnifiedInstallUpdateRequest request,
        List<string> plan,
        UnifiedUpdateStatusInfo status)
    {
        var checkpoint = new UnifiedInstallCheckpoint
        {
            OrchestrationId = orchestrationId,
            StartedAtUtc = startedAt,
            LastUpdatedAtUtc = startedAt,
            IsInstalling = false,
            Force = request.Force,
            RequestedBy = request.RequestedBy,
            Components = plan,
            CurrentComponentIndex = 0,
            ComponentStates = new List<ComponentCheckpointState>()
        };

        foreach (var component in plan)
        {
            var componentStatus = status.Components.FirstOrDefault(c =>
                string.Equals(c.Component, component, StringComparison.OrdinalIgnoreCase));

            checkpoint.ComponentStates.Add(new ComponentCheckpointState
            {
                Component = component,
                State = ComponentInstallState.Pending,
                TargetVersion = componentStatus?.LatestVersion ?? "0.0.0.0",
                DownloadUrl = componentStatus?.DownloadUrl ?? string.Empty,
                Sha256 = componentStatus?.Sha256 ?? string.Empty,
                LocalZipPath = null,
                ErrorMessage = null,
                UpdatedAtUtc = null
            });
        }

        return checkpoint;
    }

    private List<string> BuildPlan(UnifiedInstallUpdateRequest request, UnifiedUpdateStatusInfo status)
    {
        var updateable = status.Components
            .Where(component => component.UpdateAvailable)
            .Select(component => component.Component)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogDebug("[DIAG] BuildPlan: Updateable components: {List}", string.Join(", ", updateable));

        var unavailable = status.Components
            .Where(component => !component.UpdateAvailable)
            .Select(component => $"{component.Component}(Current={component.CurrentVersion}, Latest={component.LatestVersion})")
            .ToList();
        if (unavailable.Count > 0)
        {
            _logger.LogDebug("[DIAG] BuildPlan: Components skipped due to UpdateAvailable=false: {List}", string.Join(", ", unavailable));
        }

        if (request.UpdateAll)
        {
            var plan = DefaultAllOrder.Where(updateable.Contains).ToList();
            _logger.LogDebug("[DIAG] BuildPlan: Final ordered plan: {List}", string.Join(", ", plan));
            var droppedByOrder = updateable.Where(component => !DefaultAllOrder.Contains(component, StringComparer.OrdinalIgnoreCase)).ToList();
            if (droppedByOrder.Count > 0)
            {
                _logger.LogDebug("[DIAG] BuildPlan: Updateable components not in default order and therefore not scheduled: {List}", string.Join(", ", droppedByOrder));
            }

            return plan;
        }

        if (request.Components.Count == 0)
        {
            _logger.LogDebug("[DIAG] BuildPlan: No requested components and UpdateAll=false; returning empty plan.");
            return new List<string>();
        }

        var explicitlySkipped = request.Components.Where(component => !updateable.Contains(component)).ToList();
        if (explicitlySkipped.Count > 0)
        {
            _logger.LogDebug("[DIAG] BuildPlan: Requested components skipped because UpdateAvailable=false or missing: {List}", string.Join(", ", explicitlySkipped));
        }

        var explicitPlan = request.Components
            .Where(updateable.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger.LogDebug("[DIAG] BuildPlan: Final ordered plan: {List}", string.Join(", ", explicitPlan));
        return explicitPlan;
    }

    private static double ComputePercent(int completed, int total)
    {
        if (total <= 0)
            return 0;

        return Math.Clamp((completed / (double)total) * 100d, 0d, 100d);
    }

    private Task<(bool Success, string? ErrorMessage)> StopComponentBeforeUpdateAsync(string component, CancellationToken cancellationToken)
    {
        if (_stopComponentBeforeUpdateOverride != null)
        {
            return _stopComponentBeforeUpdateOverride(component, cancellationToken);
        }

        return component.ToLowerInvariant() switch
        {
            "ui" => StopUiAsync(cancellationToken),
            "server" => StopServerAsync(cancellationToken),
            "agent" => StopAgentAsync(cancellationToken),
            _ => Task.FromResult((true, (string?)null))
        };
    }

    private async Task<(bool Success, string? ErrorMessage)> StopUiAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ORCH] Stopping UI before update...");

        var processes = Process.GetProcessesByName(UiProcessName);
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    _ = process.CloseMainWindow();
                }
            }
            catch
            {
            }
        }

        _logger.LogInformation("[ORCH] Waiting for UI to exit...");
        var exitedGracefully = await WaitForProcessesToExitAsync(UiProcessName, ComponentStopTimeout, cancellationToken);
        if (!exitedGracefully)
        {
            KillProcessesByName(UiProcessName);
            var exitedAfterKill = await WaitForProcessesToExitAsync(UiProcessName, TimeSpan.FromSeconds(5), cancellationToken);
            if (!exitedAfterKill)
            {
                return (false, "[ORCH] UI stop verification failed. StorageWatchUI process is still running.");
            }
        }

        _logger.LogInformation("[ORCH] UI stopped.");
        return (true, null);
    }

    private async Task<(bool Success, string? ErrorMessage)> StopServerAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ORCH] Stopping Server before update...");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                _logger.LogInformation("[ORCH] Waiting for Server to exit...");
                var serviceStop = _stopWindowsService(ServerServiceName, ComponentStopTimeout);
                if (!serviceStop.Success)
                {
                    _logger.LogWarning("[ORCH] Server service stop attempt failed: {ErrorMessage}", serviceStop.ErrorMessage);
                }
            }
        }
        catch
        {
            // If service isn't installed/accessible, continue with process-based stop fallback.
        }

        var exited = await WaitForProcessesToExitAsync(ServerProcessName, ComponentStopTimeout, cancellationToken);
        if (!exited)
        {
            KillProcessesByName(ServerProcessName);
            var exitedAfterKill = await WaitForProcessesToExitAsync(ServerProcessName, TimeSpan.FromSeconds(5), cancellationToken);
            if (!exitedAfterKill)
            {
                return (false, "[ORCH] Server stop verification failed. StorageWatchServer process is still running.");
            }
        }

        _logger.LogInformation("[ORCH] Server stopped.");
        return (true, null);
    }

    private async Task<(bool Success, string? ErrorMessage)> StopAgentAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ORCH] Stopping Agent before update...");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                _logger.LogInformation("[ORCH] Waiting for Agent to exit...");
                var serviceStop = _stopWindowsService(AgentServiceName, ComponentStopTimeout);
                if (!serviceStop.Success)
                {
                    _logger.LogWarning("[ORCH] Agent service stop attempt failed: {ErrorMessage}", serviceStop.ErrorMessage);
                }
            }
        }
        catch
        {
            // Continue with process-based verification fallback.
        }

        var exited = await WaitForProcessesToExitAsync(AgentProcessName, ComponentStopTimeout, cancellationToken);
        if (!exited)
        {
            KillProcessesByName(AgentProcessName);
            var exitedAfterKill = await WaitForProcessesToExitAsync(AgentProcessName, TimeSpan.FromSeconds(5), cancellationToken);
            if (!exitedAfterKill)
            {
                return (false, "[ORCH] Agent stop verification failed. StorageWatchAgent process is still running.");
            }
        }

        _logger.LogInformation("[ORCH] Agent stopped.");
        return (true, null);
    }

    private static (bool Success, string? ErrorMessage) StopWindowsServiceByName(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            _ = service.Status;
            if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> WaitForProcessesToExitAsync(string processName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var running = Process.GetProcessesByName(processName).Any(process => !process.HasExited);
            if (!running)
            {
                return true;
            }

            await _delayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return !Process.GetProcessesByName(processName).Any(process => !process.HasExited);
    }

    private static void KillProcessesByName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private void SetProgress(string orchestrationId, string phase, string component, string status, double progressPercent, bool isIndeterminate)
    {
        lock (_stateGate)
        {
            _latestProgress = new UnifiedUpdateProgressInfo
            {
                OrchestrationId = orchestrationId,
                Phase = phase,
                Component = component,
                Status = status,
                ProgressPercent = progressPercent,
                IsIndeterminate = isIndeterminate
            };
        }
    }

    private void SetLastResult(UnifiedUpdateInstallResult result)
    {
        lock (_stateGate)
        {
            _lastResult = Clone(result);
        }
    }

    private static UnifiedUpdateProgressInfo Clone(UnifiedUpdateProgressInfo source)
    {
        return new UnifiedUpdateProgressInfo
        {
            ContractVersion = source.ContractVersion,
            OrchestrationId = source.OrchestrationId,
            Phase = source.Phase,
            Component = source.Component,
            Status = source.Status,
            ProgressPercent = source.ProgressPercent,
            IsIndeterminate = source.IsIndeterminate
        };
    }

    private static UnifiedUpdateInstallResult Clone(UnifiedUpdateInstallResult source)
    {
        return new UnifiedUpdateInstallResult
        {
            ContractVersion = source.ContractVersion,
            OrchestrationId = source.OrchestrationId,
            Success = source.Success,
            RestartRequired = source.RestartRequired,
            ErrorMessage = source.ErrorMessage,
            StartedAtUtc = source.StartedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            UpdatedComponents = source.UpdatedComponents.ToList(),
            FailedComponents = source.FailedComponents.ToList()
        };
    }

    private static (bool Success, string UpdaterPath, string WorkingDirectory, List<string> Arguments, string? Error) PrepareInvocation(string component, UnifiedUpdateComponentStatus componentStatus, string zipPath, ResolvedInstallPaths paths)
    {
        if (!File.Exists(paths.UpdaterExecutablePath))
        {
            return (false, string.Empty, string.Empty, new List<string>(), $"Updater executable was not found at '{paths.UpdaterExecutablePath}'.");
        }

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "StorageWatchUnifiedUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        if (!string.Equals(component, "updater", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);
        }

        var manifestPath = EnsureStagingManifest(stagingDirectory, component, componentStatus);
        var targetDirectory = ResolveTargetDirectory(component, paths);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return (false, string.Empty, string.Empty, new List<string>(), $"Unknown component target: '{component}'.");
        }

        var args = BuildUpdaterArguments(component, stagingDirectory, targetDirectory, manifestPath);
        return (true, paths.UpdaterExecutablePath, Path.GetDirectoryName(paths.UpdaterExecutablePath) ?? AppContext.BaseDirectory, args, null);
    }

    private static (bool Success, string? ErrorMessage) RunUpdaterProcess(string updaterPath, IReadOnlyList<string> args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo);
        if (process == null)
        {
            return (false, "Updater process could not be started.");
        }

        var exited = process.WaitForExit((int)UpdaterInvocationTimeout.TotalMilliseconds);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (false, "Updater process timed out.");
        }

        if (process.ExitCode != 0)
        {
            return (false, $"Updater process failed with exit code {process.ExitCode}.");
        }

        return (true, null);
    }

    private static (bool Success, int? ProcessId, string? ErrorMessage) RunUpdaterProcessDetached(string updaterPath, IReadOnlyList<string> args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo);
        if (process == null)
        {
            return (false, null, "Updater process could not be started.");
        }

        return (true, process.Id, null);
    }

    private static string ResolveTargetDirectory(string component, ResolvedInstallPaths paths)
    {
        return component.ToLowerInvariant() switch
        {
            "agent" => paths.AgentDirectory,
            "server" => paths.ServerDirectory,
            "ui" => paths.UiDirectory,
            "updater" => paths.UpdaterDirectory,
            _ => string.Empty
        };
    }

    private static List<string> BuildUpdaterArguments(string component, string source, string target, string manifest)
    {
        var updateFlag = component.ToLowerInvariant() switch
        {
            "agent" => "--update-agent",
            "server" => "--update-server",
            "ui" => "--update-ui",
            "updater" => "--self-update-stage",
            _ => string.Empty
        };

        var restartFlag = component.ToLowerInvariant() switch
        {
            "agent" => "--restart-agent",
            "server" => "--restart-server",
            "ui" => "--restart-ui",
            _ => string.Empty
        };

        if (string.Equals(component, "updater", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "--self-update-stage", "--manifest", manifest };
        }

        var args = new List<string> { updateFlag, "--source", source, "--target", target, "--manifest", manifest };
        if (!string.IsNullOrWhiteSpace(restartFlag))
        {
            args.Add(restartFlag);
        }

        return args;
    }

    private static string EnsureStagingManifest(string stagingDirectory, string component, UnifiedUpdateComponentStatus componentStatus)
    {
        var existingManifest = Directory
            .EnumerateFiles(stagingDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(existingManifest))
            return existingManifest;

        var manifestPath = Path.Combine(stagingDirectory, "manifest.json");
        var manifestJson = string.Equals(component, "updater", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new
            {
                version = componentStatus.LatestVersion,
                updater = new
                {
                    version = componentStatus.LatestVersion,
                    downloadUrl = componentStatus.DownloadUrl,
                    sha256 = componentStatus.Sha256
                }
            })
            : JsonSerializer.Serialize(new
            {
                component,
                createdUtc = DateTimeOffset.UtcNow
            });
        File.WriteAllText(manifestPath, manifestJson);
        return manifestPath;
    }

}