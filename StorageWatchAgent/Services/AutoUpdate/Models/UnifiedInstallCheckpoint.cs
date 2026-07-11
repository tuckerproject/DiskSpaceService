namespace StorageWatchAgent.Services.AutoUpdate.Models;

/// <summary>
/// Persisted state of a unified install orchestration for checkpoint/resume support.
/// </summary>
public class UnifiedInstallCheckpoint
{
    /// <summary>
    /// Schema version for persisted checkpoint compatibility.
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// Unique identifier for this orchestration instance.
    /// </summary>
    public string OrchestrationId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the install was initiated.
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>
    /// Timestamp of the last checkpoint update.
    /// </summary>
    public DateTimeOffset LastUpdatedAtUtc { get; set; }

    /// <summary>
    /// Whether this install is currently in progress.
    /// </summary>
    public bool IsInstalling { get; set; }

    /// <summary>
    /// Whether force flag was set for this install.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Who requested this install (e.g., "UI", "Auto", "Manual").
    /// </summary>
    public string? RequestedBy { get; set; }

    /// <summary>
    /// Ordered list of components to install (e.g., "Updater", "Server", "UI", "Agent").
    /// </summary>
    public List<string> Components { get; set; } = new();

    /// <summary>
    /// Current component index in the install sequence.
    /// </summary>
    public int CurrentComponentIndex { get; set; }

    /// <summary>
    /// Per-component install state.
    /// </summary>
    public List<ComponentCheckpointState> ComponentStates { get; set; } = new();

    /// <summary>
    /// Overall error message if the orchestration failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Timestamp when detached updater handoff started.
    /// </summary>
    public DateTimeOffset? HandoffStartedAtUtc { get; set; }

    /// <summary>
    /// Timestamp when Agent shutdown was requested for handoff.
    /// </summary>
    public DateTimeOffset? AgentExitRequestedAtUtc { get; set; }

    /// <summary>
    /// Timestamp when updater completed Agent stop-copy-start handoff.
    /// </summary>
    public DateTimeOffset? HandoffCompletedAtUtc { get; set; }

    /// <summary>
    /// Process ID of detached updater for diagnostics and recovery.
    /// </summary>
    public int? UpdaterProcessId { get; set; }

    /// <summary>
    /// Current handoff lifecycle state.
    /// </summary>
    public AgentHandoffState HandoffState { get; set; } = AgentHandoffState.None;

    /// <summary>
    /// Number of startup resume attempts for loop prevention.
    /// </summary>
    public int ResumeAttemptCount { get; set; }
}

public enum AgentHandoffState
{
    None,
    Started,
    ExitRequested,
    Completed,
    Failed
}

/// <summary>
/// State of a single component within a checkpoint.
/// </summary>
public class ComponentCheckpointState
{
    /// <summary>
    /// Component name (e.g., "Agent", "UI").
    /// </summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>
    /// Current state: Pending, InProgress, Completed, Failed.
    /// </summary>
    public ComponentInstallState State { get; set; } = ComponentInstallState.Pending;

    /// <summary>
    /// Version being installed.
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    /// Download URL for this component.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Local path to downloaded ZIP (if downloaded).
    /// </summary>
    public string? LocalZipPath { get; set; }

    /// <summary>
    /// SHA-256 hash for verification.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Error message if this component failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Timestamp when this component state was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Install state for a single component.
/// </summary>
public enum ComponentInstallState
{
    Pending,
    InProgress,
    Completed,
    Failed
}
