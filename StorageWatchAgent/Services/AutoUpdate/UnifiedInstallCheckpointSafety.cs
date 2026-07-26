using Microsoft.Extensions.Logging;
using StorageWatch.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StorageWatchAgent.Services.AutoUpdate;

public enum UnifiedInstallResumeState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Stale
}

public sealed class UnifiedInstallCheckpointLoadResult
{
    public bool Exists { get; init; }

    public bool IsCorrupted { get; init; }

    public UnifiedInstallCheckpoint? Checkpoint { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class UnifiedInstallCheckpointValidationResult
{
    public UnifiedInstallResumeState State { get; init; }

    public bool IsCorrupted { get; init; }

    public bool ShouldResume => State == UnifiedInstallResumeState.InProgress;

    public bool ShouldDelete => State is UnifiedInstallResumeState.Completed or UnifiedInstallResumeState.Failed or UnifiedInstallResumeState.Stale;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();
}

public interface IUnifiedInstallCheckpointValidator
{
    UnifiedInstallCheckpointValidationResult Validate(UnifiedInstallCheckpoint checkpoint);
}

public sealed class UnifiedInstallCheckpointValidator : IUnifiedInstallCheckpointValidator
{
    private const int CurrentCheckpointSchemaVersion = 4;
    private static readonly TimeSpan HandoffCompletionFreshness = TimeSpan.FromHours(1);
    private static readonly TimeSpan HandoffInProgressGrace = TimeSpan.FromSeconds(30);
    private const int MaxResumeAttempts = 3;
    private static readonly TimeSpan StaleCheckpointAge = TimeSpan.FromMinutes(15);
    private readonly global::StorageWatch.Services.AutoUpdate.IInstallPathResolver _installPathResolver;
    private readonly ILogger<UnifiedInstallCheckpointValidator> _logger;

    public UnifiedInstallCheckpointValidator(
        global::StorageWatch.Services.AutoUpdate.IInstallPathResolver installPathResolver,
        ILogger<UnifiedInstallCheckpointValidator> logger)
    {
        _installPathResolver = installPathResolver ?? throw new ArgumentNullException(nameof(installPathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public UnifiedInstallCheckpointValidationResult Validate(UnifiedInstallCheckpoint checkpoint)
    {
        _logger.LogInformation("[AUTOUPDATE-VALIDATION] Checkpoint validation entered. OrchestrationId={OrchestrationId}, SchemaVersion={SchemaVersion}, IsInstalling={IsInstalling}, ResumeAttemptCount={ResumeAttemptCount}, HandoffState={HandoffState}, HandoffStartedAtUtc={HandoffStartedAtUtc}, AgentExitRequestedAtUtc={AgentExitRequestedAtUtc}, HandoffCompletedAtUtc={HandoffCompletedAtUtc}, RestartUIRequested={RestartUIRequested}, RestartServerRequested={RestartServerRequested}, ComponentCount={ComponentCount}, ComponentStateCount={ComponentStateCount}",
            checkpoint?.OrchestrationId ?? "<null>",
            checkpoint?.SchemaVersion,
            checkpoint?.IsInstalling,
            checkpoint?.ResumeAttemptCount,
            checkpoint?.HandoffState,
            checkpoint?.HandoffStartedAtUtc,
            checkpoint?.AgentExitRequestedAtUtc,
            checkpoint?.HandoffCompletedAtUtc,
            checkpoint?.RestartUIRequested,
            checkpoint?.RestartServerRequested,
            checkpoint?.Components?.Count,
            checkpoint?.ComponentStates?.Count);

        var result = ValidateCore(checkpoint);
        _logger.LogInformation("[AUTOUPDATE-VALIDATION] Checkpoint validation completed. OrchestrationId={OrchestrationId}, State={State}, ShouldResume={ShouldResume}, ShouldDelete={ShouldDelete}, IsCorrupted={IsCorrupted}, Reason={Reason}, Signals={Signals}",
            checkpoint?.OrchestrationId ?? "<null>",
            result.State,
            result.ShouldResume,
            result.ShouldDelete,
            result.IsCorrupted,
            result.Reason,
            string.Join(" | ", result.Signals));
        return result;
    }

    private UnifiedInstallCheckpointValidationResult ValidateCore(UnifiedInstallCheckpoint checkpoint)
    {
        if (checkpoint == null)
        {
            return Corrupted("Checkpoint data was null.");
        }

        if (checkpoint.SchemaVersion <= 0 || checkpoint.SchemaVersion > CurrentCheckpointSchemaVersion)
        {
            return Corrupted($"Unsupported checkpoint schema version: {checkpoint.SchemaVersion}.");
        }

        if (checkpoint.ResumeAttemptCount > MaxResumeAttempts)
        {
            return Stale($"Checkpoint exceeded max resume attempts ({MaxResumeAttempts}).", new[] { $"resumeAttempts={checkpoint.ResumeAttemptCount}" }, isCorrupted: false);
        }

        if (checkpoint.HandoffCompletedAtUtc.HasValue)
        {
            if (!checkpoint.HandoffStartedAtUtc.HasValue)
            {
                return Stale("Handoff completion marker exists without handoff start marker.", Array.Empty<string>(), isCorrupted: true);
            }

            if (checkpoint.HandoffCompletedAtUtc.Value < checkpoint.HandoffStartedAtUtc.Value)
            {
                return Stale("Handoff completion timestamp is earlier than handoff start timestamp.", Array.Empty<string>(), isCorrupted: true);
            }

            if (checkpoint.AgentExitRequestedAtUtc.HasValue)
            {
                if (checkpoint.AgentExitRequestedAtUtc.Value < checkpoint.HandoffStartedAtUtc.Value)
                {
                    return Stale("Agent exit-requested timestamp is earlier than handoff start timestamp.", Array.Empty<string>(), isCorrupted: true);
                }

                if (checkpoint.HandoffCompletedAtUtc.Value < checkpoint.AgentExitRequestedAtUtc.Value)
                {
                    return Stale("Handoff completion timestamp is earlier than Agent exit-requested timestamp.", Array.Empty<string>(), isCorrupted: true);
                }
            }

            var completionAge = DateTimeOffset.UtcNow - checkpoint.HandoffCompletedAtUtc.Value;
            if (completionAge > HandoffCompletionFreshness)
            {
                return Stale("Handoff completion marker is stale.", new[] { $"handoffCompletionAge={completionAge.TotalMinutes:F1}m" }, isCorrupted: false);
            }

            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Completed,
                Reason = "Checkpoint contains valid handoff-complete marker.",
                Signals = new[]
                {
                    $"handoffStartedAt={checkpoint.HandoffStartedAtUtc.Value:O}",
                    $"handoffCompletedAt={checkpoint.HandoffCompletedAtUtc.Value:O}"
                }
            };
        }

        if (checkpoint.HandoffState != AgentHandoffState.None
            || checkpoint.HandoffStartedAtUtc.HasValue
            || checkpoint.AgentExitRequestedAtUtc.HasValue)
        {
            if (!checkpoint.HandoffStartedAtUtc.HasValue)
            {
                return Stale("Partial handoff state exists without handoff start marker.", Array.Empty<string>(), isCorrupted: true);
            }

            if (checkpoint.HandoffState == AgentHandoffState.ExitRequested && !checkpoint.AgentExitRequestedAtUtc.HasValue)
            {
                return Stale("Handoff exit-requested state is missing AgentExitRequestedAtUtc marker.", Array.Empty<string>(), isCorrupted: true);
            }

            if (checkpoint.AgentExitRequestedAtUtc.HasValue
                && checkpoint.AgentExitRequestedAtUtc.Value < checkpoint.HandoffStartedAtUtc.Value)
            {
                return Stale("Agent exit-requested timestamp is earlier than handoff start timestamp.", Array.Empty<string>(), isCorrupted: true);
            }

            var handoffAgeSource = checkpoint.AgentExitRequestedAtUtc ?? checkpoint.HandoffStartedAtUtc.Value;
            var handoffAge = DateTimeOffset.UtcNow - handoffAgeSource;
            var updaterProcessRunning = checkpoint.UpdaterProcessId.HasValue
                                        && IsProcessRunning(checkpoint.UpdaterProcessId.Value);

            if (updaterProcessRunning || handoffAge <= HandoffInProgressGrace)
            {
                return new UnifiedInstallCheckpointValidationResult
                {
                    State = UnifiedInstallResumeState.InProgress,
                    Reason = "In-progress handoff markers are within grace window or updater process is still active.",
                    Signals = new[]
                    {
                        $"handoffState={checkpoint.HandoffState}",
                        $"handoffStartedAt={checkpoint.HandoffStartedAtUtc.Value:O}",
                        $"handoffAgeSeconds={handoffAge.TotalSeconds:F1}",
                        $"updaterRunning={updaterProcessRunning}"
                    }
                };
            }

            return Stale("In-progress handoff is missing completion marker and is beyond grace without active updater.", new[]
            {
                $"handoffState={checkpoint.HandoffState}",
                $"handoffStartedAt={checkpoint.HandoffStartedAtUtc.Value:O}",
                $"handoffAgeSeconds={handoffAge.TotalSeconds:F1}",
                $"updaterRunning={updaterProcessRunning}"
            }, isCorrupted: false);
        }

        var signals = new List<string>();
        var structuralIssues = new List<string>();

        if (string.IsNullOrWhiteSpace(checkpoint.OrchestrationId))
        {
            structuralIssues.Add("missing orchestration id");
        }

        if (checkpoint.Components.Count == 0)
        {
            structuralIssues.Add("no components listed");
        }

        if (checkpoint.ComponentStates.Count == 0)
        {
            structuralIssues.Add("no component states listed");
        }

        if (checkpoint.Components.Count != checkpoint.ComponentStates.Count)
        {
            structuralIssues.Add("component and component state counts differ");
        }

        if (checkpoint.CurrentComponentIndex < 0 || checkpoint.CurrentComponentIndex >= Math.Max(checkpoint.ComponentStates.Count, 1))
        {
            structuralIssues.Add($"current component index {checkpoint.CurrentComponentIndex} is out of range");
        }

        if (checkpoint.ComponentStates.Any(state => string.IsNullOrWhiteSpace(state.Component)))
        {
            structuralIssues.Add("at least one component state has no component name");
        }

        if (checkpoint.ComponentStates
            .Select(state => state.Component.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != checkpoint.ComponentStates.Count)
        {
            structuralIssues.Add("duplicate component entries found");
        }

        if (structuralIssues.Count > 0)
        {
            return Corrupted(string.Join("; ", structuralIssues), structuralIssues);
        }

        if (checkpoint.ComponentStates.All(state => state.State == ComponentInstallState.Completed))
        {
            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Completed,
                Reason = "All component states are completed.",
                Signals = signals
            };
        }

        if (checkpoint.ComponentStates.Any(state => state.State == ComponentInstallState.Failed) || !string.IsNullOrWhiteSpace(checkpoint.ErrorMessage))
        {
            signals.Add("failure recorded in checkpoint state");
            if (!string.IsNullOrWhiteSpace(checkpoint.ErrorMessage))
            {
                signals.Add($"error='{checkpoint.ErrorMessage}'");
            }

            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Failed,
                Reason = "Checkpoint captured a failed update state.",
                Signals = signals
            };
        }

        var checkpointAgeSource = checkpoint.LastUpdatedAtUtc == default ? checkpoint.StartedAtUtc : checkpoint.LastUpdatedAtUtc;
        var checkpointAge = DateTimeOffset.UtcNow - checkpointAgeSource;
        if (checkpointAge > StaleCheckpointAge)
        {
            signals.Add($"age={checkpointAge.TotalMinutes:F1}m");
            return Stale($"Checkpoint is older than {StaleCheckpointAge.TotalMinutes:F0} minutes.", signals, isCorrupted: false);
        }

        var paths = _installPathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(paths.InstallRoot) || !Directory.Exists(paths.InstallRoot))
        {
            signals.Add($"install root missing: {paths.InstallRoot}");
            return Stale("Install root is missing.", signals, isCorrupted: false);
        }

        if (string.IsNullOrWhiteSpace(paths.UpdaterExecutablePath) || !File.Exists(paths.UpdaterExecutablePath))
        {
            signals.Add($"updater missing: {paths.UpdaterExecutablePath}");
            return Stale("Updater executable is missing.", signals, isCorrupted: false);
        }

        foreach (var directory in EnumerateExpectedDirectories(paths))
        {
            if (!Directory.Exists(directory))
            {
                signals.Add($"directory missing: {directory}");
                return Stale("One or more expected install directories are missing.", signals, isCorrupted: false);
            }
        }

        for (var i = 0; i < checkpoint.CurrentComponentIndex; i++)
        {
            if (checkpoint.ComponentStates[i].State != ComponentInstallState.Completed)
            {
                signals.Add($"prior component not completed: {checkpoint.ComponentStates[i].Component}={checkpoint.ComponentStates[i].State}");
                return Stale("Checkpoint sequencing is inconsistent.", signals, isCorrupted: true);
            }
        }

        var currentState = checkpoint.ComponentStates[checkpoint.CurrentComponentIndex];
        signals.Add($"current={currentState.Component}:{currentState.State}");

        if (currentState.State == ComponentInstallState.Completed)
        {
            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Completed,
                Reason = "Current component already completed.",
                Signals = signals
            };
        }

        if (currentState.State == ComponentInstallState.Pending)
        {
            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Pending,
                Reason = "Checkpoint exists but the active component has not started yet.",
                Signals = signals
            };
        }

        if (currentState.State != ComponentInstallState.InProgress)
        {
            return Stale($"Current component is in an unexpected state: {currentState.State}.", signals, isCorrupted: true);
        }

        if (!TryResolveComponentExecutable(paths, currentState.Component, out var executablePath))
        {
            signals.Add($"unmapped component: {currentState.Component}");
            return Stale("Checkpoint references an unknown component.", signals, isCorrupted: true);
        }

        if (!File.Exists(executablePath))
        {
            signals.Add($"component executable missing: {executablePath}");
            return Stale($"Component executable missing for {currentState.Component}.", signals, isCorrupted: false);
        }

        if (!TryGetFileVersion(executablePath, out var installedVersion, out var installedVersionText))
        {
            signals.Add($"version unreadable: {executablePath}");
            return Stale($"Unable to read installed version for {currentState.Component}.", signals, isCorrupted: false);
        }

        if (!Version.TryParse(currentState.TargetVersion, out var targetVersion))
        {
            signals.Add($"target version invalid: {currentState.TargetVersion}");
            return Stale("Checkpoint target version is invalid.", signals, isCorrupted: true);
        }

        signals.Add($"installed={installedVersionText}");
        signals.Add($"target={targetVersion}");

        if (targetVersion == installedVersion)
        {
            return new UnifiedInstallCheckpointValidationResult
            {
                State = UnifiedInstallResumeState.Completed,
                Reason = "Target version already matches the installed version.",
                Signals = signals
            };
        }

        if (targetVersion < installedVersion)
        {
            return Stale("Checkpoint target version is older than the installed version.", signals, isCorrupted: false);
        }

        if (!string.IsNullOrWhiteSpace(currentState.LocalZipPath) && !File.Exists(currentState.LocalZipPath))
        {
            signals.Add($"staged package missing: {currentState.LocalZipPath}");
            _logger.LogWarning(
                "[AUTOUPDATE] Resume validation found missing staged package for orchestration {OrchestrationId} component {Component}; resume can continue by redownloading.",
                checkpoint.OrchestrationId,
                currentState.Component);
        }

        if (checkpoint.Components.Any(component => string.IsNullOrWhiteSpace(component)))
        {
            return Stale("Checkpoint contains an empty component entry.", signals, isCorrupted: true);
        }

        if (checkpoint.Components.Count != checkpoint.ComponentStates.Count)
        {
            return Stale("Checkpoint component metadata is inconsistent.", signals, isCorrupted: true);
        }

        return new UnifiedInstallCheckpointValidationResult
        {
            State = UnifiedInstallResumeState.InProgress,
            Reason = "Checkpoint passed freshness, structure, version, and artifact validation.",
            Signals = signals
        };
    }

    private static UnifiedInstallCheckpointValidationResult Corrupted(string reason, IEnumerable<string>? signals = null)
    {
        return Stale(reason, signals, isCorrupted: true);
    }

    private static UnifiedInstallCheckpointValidationResult Stale(string reason, IEnumerable<string>? signals, bool isCorrupted)
    {
        return new UnifiedInstallCheckpointValidationResult
        {
            State = UnifiedInstallResumeState.Stale,
            IsCorrupted = isCorrupted,
            Reason = reason,
            Signals = signals?.ToArray() ?? Array.Empty<string>()
        };
    }

    private static bool TryResolveComponentExecutable(global::StorageWatch.Services.AutoUpdate.ResolvedInstallPaths paths, string component, out string executablePath)
    {
        executablePath = string.Empty;

        switch (component.Trim().ToLowerInvariant())
        {
            case "agent":
                executablePath = Path.Combine(paths.AgentDirectory, "StorageWatchAgent.exe");
                return true;
            case "server":
                executablePath = Path.Combine(paths.ServerDirectory, "StorageWatchServer.exe");
                return true;
            case "ui":
                executablePath = Path.Combine(paths.UiDirectory, "StorageWatchUI.exe");
                return true;
            case "updater":
                executablePath = Path.Combine(paths.UpdaterDirectory, "StorageWatch.Updater.exe");
                return true;
            default:
                return false;
        }
    }

    private static IEnumerable<string> EnumerateExpectedDirectories(global::StorageWatch.Services.AutoUpdate.ResolvedInstallPaths paths)
    {
        yield return paths.AgentDirectory;
        yield return paths.ServerDirectory;
        yield return paths.UiDirectory;
        yield return paths.UpdaterDirectory;
    }

    private static bool TryGetFileVersion(string filePath, out Version version, out string versionText)
    {
        version = new Version(0, 0, 0, 0);
        versionText = string.Empty;

        try
        {
            var fileVersion = FileVersionInfo.GetVersionInfo(filePath).FileVersion;
            versionText = string.IsNullOrWhiteSpace(fileVersion) ? string.Empty : fileVersion;
            return !string.IsNullOrWhiteSpace(fileVersion) && Version.TryParse(fileVersion, out version);
        }
        catch
        {
            return false;
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
