using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StorageWatch.Shared.Update.Models;

namespace StorageWatch.Updater;

/// <summary>
/// Manages updater self-update process.
/// Downloads new updater ZIP, extracts to temp, replaces current updater folder, and restarts.
/// </summary>
internal class SelfUpdateManager
{
    private const int ReplaceRetryCount = 15;
    private const int ReplaceRetryDelayMs = 500;
    private const int ParentExitPollDelayMs = 200;
    private const int ParentExitWaitTimeoutMs = 30000;

    private readonly string _currentUpdaterFolder;
    private readonly string _updaterExePath;
    private readonly HttpClient _httpClient;
    private readonly IProcessLauncher _processLauncher;
    private readonly Action<int> _sleepAction;
    private readonly Action<string>? _diagnosticLogger;

    public SelfUpdateManager(Action<string>? diagnosticLogger = null)
    {
        _updaterExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine updater executable path.");
        _currentUpdaterFolder = Path.GetDirectoryName(_updaterExePath)
            ?? throw new InvalidOperationException("Could not determine updater folder.");
        _httpClient = new HttpClient();
        _processLauncher = new ProcessLauncher();
        _sleepAction = Thread.Sleep;
        _diagnosticLogger = diagnosticLogger;
        LogDiag($"SelfUpdateManager initialized. ProcessPath={_updaterExePath}, CurrentUpdaterFolder={_currentUpdaterFolder}");
    }

    internal SelfUpdateManager(
        string updaterExePath,
        string currentUpdaterFolder,
        HttpClient httpClient,
        IProcessLauncher processLauncher,
        Action<int>? sleepAction = null,
        Action<string>? diagnosticLogger = null)
    {
        _updaterExePath = updaterExePath;
        _currentUpdaterFolder = currentUpdaterFolder;
        _httpClient = httpClient;
        _processLauncher = processLauncher;
        _sleepAction = sleepAction ?? Thread.Sleep;
        _diagnosticLogger = diagnosticLogger;
        LogDiag($"SelfUpdateManager initialized (injected). ProcessPath={_updaterExePath}, CurrentUpdaterFolder={_currentUpdaterFolder}");
    }

    private void LogDiag(string message)
    {
        _diagnosticLogger?.Invoke($"[DIAG] {message}");
    }

    /// <summary>
    /// Checks if updater self-update is needed by comparing local version to manifest version.
    /// </summary>
    public bool IsUpdateAvailable(ComponentUpdateInfo updaterManifestEntry)
    {
        LogDiag($"IsUpdateAvailable invoked. ManifestVersionRaw={updaterManifestEntry.Version}, ProcessPath={_updaterExePath}");
        var currentVersion = GetCurrentUpdaterVersion();
        LogVersionMetadata(_updaterExePath, "updater-current");
        if (!Version.TryParse(updaterManifestEntry.Version, out var manifestVersion))
        {
            LogDiag($"Manifest updater version parse failed. ManifestVersionRaw={updaterManifestEntry.Version}");
            return false;
        }

        var isUpdateAvailable = manifestVersion > currentVersion;
        LogDiag($"Self-update comparison: Manifest={manifestVersion}, Current={currentVersion}, IsUpdateAvailable={isUpdateAvailable}");
        return isUpdateAvailable;
    }

    public async Task<bool> RunSelfUpdateStageAsync(
        string manifestPath,
        UpdaterArguments currentArguments,
        CancellationToken cancellationToken = default)
    {
        LogDiag($"RunSelfUpdateStageAsync start. ManifestPath={manifestPath}, Exists={File.Exists(manifestPath)}");
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                manifestJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Manifest could not be parsed.");

        if (manifest.Updater == null)
            throw new InvalidOperationException("Manifest is missing updater component information.");

        LogDiag($"Manifest updater version: {manifest.Updater.Version}, DownloadUrl={manifest.Updater.DownloadUrl}");

        return await RunLegacySelfUpdateStageAsync(manifest.Updater, currentArguments, cancellationToken);
    }

    public async Task<bool> RunLegacySelfUpdateStageAsync(
        ComponentUpdateInfo updaterManifestEntry,
        UpdaterArguments currentArguments,
        CancellationToken cancellationToken = default)
    {
        LogDiag($"RunLegacySelfUpdateStageAsync start. ManifestUpdaterVersion={updaterManifestEntry.Version}, DownloadUrl={updaterManifestEntry.DownloadUrl}");

        if (!IsUpdateAvailable(updaterManifestEntry))
        {
            LogDiag("Self-update apply not triggered because IsUpdateAvailable=false.");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "StorageWatchUpdaterSelfUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        LogDiag($"Self-update stage paths. TempDir={tempDir}, Target={_currentUpdaterFolder}");

        try
        {
            var zipPath = Path.Combine(tempDir, "updater.zip");
            LogDiag($"Downloading updater package from {updaterManifestEntry.DownloadUrl} to {zipPath}");
            var zipBytes = await _httpClient.GetByteArrayAsync(updaterManifestEntry.DownloadUrl, cancellationToken);

            // Verify hash
            using var sha256 = SHA256.Create();
            var downloadedHash = Convert.ToHexString(sha256.ComputeHash(zipBytes)).ToLowerInvariant();
            LogDiag($"Hash verification. Expected={updaterManifestEntry.Sha256?.ToLowerInvariant()}, Actual={downloadedHash}");
            if (downloadedHash != updaterManifestEntry.Sha256.ToLowerInvariant())
            {
                throw new InvalidOperationException("Updater ZIP hash mismatch.");
            }

            await File.WriteAllBytesAsync(zipPath, zipBytes, cancellationToken);

            var stagingFolder = Path.Combine(tempDir, "staging");
            ZipFile.ExtractToDirectory(zipPath, stagingFolder, overwriteFiles: true);
            LogDiag($"Extracted self-update package. StagingFolder={stagingFolder}");

            var stagedUpdaterExe = Path.Combine(stagingFolder, "StorageWatch.Updater.exe");
            LogDiag($"Staged updater exe path. Path={stagedUpdaterExe}, Exists={File.Exists(stagedUpdaterExe)}");
            if (!File.Exists(stagedUpdaterExe))
                throw new InvalidOperationException("Staged updater executable was not found in self-update package.");

            LogVersionMetadata(stagedUpdaterExe, "updater-staged");

            var continueArgs = BuildContinuationArguments(currentArguments);
            var encodedContinuation = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(continueArgs)));
            LogDiag($"Continuation args prepared. Count={continueArgs.Length}, EncodedLength={encodedContinuation.Length}");
            var currentProcessId = Environment.ProcessId;

            var applyArgs = new[]
            {
                "--self-update-apply",
                "--self-update-staging", stagingFolder,
                "--target", _currentUpdaterFolder,
                "--wait-for-pid", currentProcessId.ToString(),
                "--continue-args", encodedContinuation
            };
            LogDiag($"Launching self-update apply. Args={string.Join(" ", applyArgs)}");

            var applyProcessStarted = _processLauncher.Start(new ProcessStartInfo
            {
                FileName = stagedUpdaterExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stagingFolder,
                ArgumentList =
                {
                    "--self-update-apply",
                    "--self-update-staging", stagingFolder,
                    "--target", _currentUpdaterFolder,
                    "--wait-for-pid", currentProcessId.ToString(),
                    "--continue-args", encodedContinuation
                }
            });

            LogDiag($"Self-update apply launch result. Started={applyProcessStarted}");

            if (!applyProcessStarted)
                throw new InvalidOperationException("Failed to launch staged updater apply process.");

            return true;
        }
        catch
        {
            if (Directory.Exists(tempDir))
            {
                LogDiag($"Self-update stage failed. Cleaning temp directory {tempDir}");
                Directory.Delete(tempDir, recursive: true);
            }
            throw;
        }
    }

    public Task<bool> RunSelfUpdateApplyAsync(UpdaterArguments arguments, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(arguments.SelfUpdateStagingPath))
            throw new InvalidOperationException("Self-update apply requires staging path.");
        if (string.IsNullOrWhiteSpace(arguments.TargetPath))
            throw new InvalidOperationException("Self-update apply requires target path.");

        var stagingFolder = Path.GetFullPath(arguments.SelfUpdateStagingPath);
        var targetFolder = Path.GetFullPath(arguments.TargetPath);
        LogDiag($"RunSelfUpdateApplyAsync start. StagingFolder={stagingFolder}, TargetFolder={targetFolder}, WaitForPid={arguments.WaitForPid?.ToString() ?? "<none>"}, ContinueArgsPresent={!string.IsNullOrWhiteSpace(arguments.ContinueArguments)}");

        if (!Directory.Exists(stagingFolder))
            throw new DirectoryNotFoundException($"Self-update staging folder not found: {stagingFolder}");

        if (arguments.WaitForPid is int parentPid)
        {
            try
            {
                WaitForProcessExit(parentPid, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LogDiag($"Parent wait canceled before replacement. ProcessId={parentPid}");
                throw;
            }
            catch (TimeoutException ex)
            {
                LogDiag($"Parent wait timed out before replacement. ProcessId={parentPid}, Message={ex.Message}");
                throw;
            }
        }

        LogDiag("Starting target replacement with retry fallback for transient non-parent locks.");
        ReplaceTargetFolderWithRetries(stagingFolder, targetFolder, cancellationToken, _sleepAction);
        LogDiag($"Self-update apply replacement complete. Source={stagingFolder}, Target={targetFolder}");

        if (!string.IsNullOrWhiteSpace(arguments.ContinueArguments))
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(arguments.ContinueArguments));
            var continuationArgs = JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
            if (continuationArgs.Length > 0)
            {
                var updatedUpdaterPath = Path.Combine(targetFolder, "StorageWatch.Updater.exe");
                LogDiag($"Launching post-apply continuation updater. Path={updatedUpdaterPath}, ArgCount={continuationArgs.Length}");
                var restart = new ProcessStartInfo
                {
                    FileName = updatedUpdaterPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = targetFolder
                };

                foreach (var continuationArg in continuationArgs)
                {
                    restart.ArgumentList.Add(continuationArg);
                }

                _processLauncher.Start(restart);
                LogDiag("Post-apply continuation launch invoked.");
            }
        }

        return Task.FromResult(true);
    }

    internal static string[] BuildContinuationArguments(UpdaterArguments currentArguments)
    {
        if (currentArguments.UpdateUI)
        {
            return BuildUpdateContinuationArguments("--update-ui", "--restart-ui", currentArguments.RestartUI, currentArguments);
        }

        if (currentArguments.UpdateAgent)
        {
            return BuildUpdateContinuationArguments("--update-agent", "--restart-agent", currentArguments.RestartAgent, currentArguments);
        }

        if (currentArguments.UpdateServer)
        {
            return BuildUpdateContinuationArguments("--update-server", "--restart-server", currentArguments.RestartServer, currentArguments);
        }

        if (currentArguments.RestartUI)
            return new[] { "--restart-ui" };

        if (currentArguments.RestartAgent)
            return new[] { "--restart-agent" };

        if (currentArguments.RestartServer)
            return new[] { "--restart-server" };

        return Array.Empty<string>();
    }

    private static string[] BuildUpdateContinuationArguments(
        string updateFlag,
        string restartFlag,
        bool restartRequested,
        UpdaterArguments currentArguments)
    {
        var args = new List<string> { updateFlag };

        if (!string.IsNullOrWhiteSpace(currentArguments.SourcePath))
        {
            args.Add("--source");
            args.Add(currentArguments.SourcePath);
        }

        if (!string.IsNullOrWhiteSpace(currentArguments.TargetPath))
        {
            args.Add("--target");
            args.Add(currentArguments.TargetPath);
        }

        if (!string.IsNullOrWhiteSpace(currentArguments.ManifestPath))
        {
            args.Add("--manifest");
            args.Add(currentArguments.ManifestPath);
        }

        if (restartRequested)
        {
            args.Add(restartFlag);

            if (string.Equals(restartFlag, "--restart-ui", StringComparison.OrdinalIgnoreCase)
                || string.Equals(restartFlag, "--restart-server", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--allow-system-restart-intent");
            }
        }

        return args.ToArray();
    }

    private static void ReplaceTargetFolderWithRetries(string sourceFolder, string targetFolder, CancellationToken cancellationToken, Action<int> sleepAction)
    {
        var targetParent = Path.GetDirectoryName(targetFolder)
            ?? throw new InvalidOperationException("Target updater folder parent could not be determined.");

        Directory.CreateDirectory(targetParent);

        Exception? lastError = null;
        for (var i = 0; i < ReplaceRetryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(targetFolder))
                {
                    Directory.Delete(targetFolder, recursive: true);
                }

                CopyDirectory(sourceFolder, targetFolder);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                sleepAction(ReplaceRetryDelayMs);
            }
        }

        throw new InvalidOperationException("Failed to apply updater self-update after retries.", lastError);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            var destinationSubDir = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(destinationSubDir);
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            var destinationSubDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationSubDir))
                Directory.CreateDirectory(destinationSubDir);

            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private void WaitForProcessExit(int processId, CancellationToken cancellationToken)
    {
        LogDiag($"Waiting for parent updater process to exit. ProcessId={processId}, TimeoutMs={ParentExitWaitTimeoutMs}");
        var elapsedMs = 0;

        try
        {
            using var process = Process.GetProcessById(processId);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    LogDiag($"Parent updater process exited. ProcessId={processId}, WaitedMs={elapsedMs}");
                    return;
                }

                if (elapsedMs >= ParentExitWaitTimeoutMs)
                {
                    throw new TimeoutException($"Timed out waiting for parent updater process {processId} to exit.");
                }

                _sleepAction(ParentExitPollDelayMs);
                elapsedMs += ParentExitPollDelayMs;
                process.Refresh();
            }
        }
        catch (ArgumentException)
        {
            LogDiag($"Parent updater process already exited before wait started. ProcessId={processId}");
        }
        catch (InvalidOperationException)
        {
            LogDiag($"Parent updater process became unavailable during wait; treating as exited. ProcessId={processId}");
        }
    }

    private Version GetCurrentUpdaterVersion()
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(_updaterExePath);
        LogDiag($"Current updater version metadata. FileVersion={versionInfo.FileVersion}, ProductVersion={versionInfo.ProductVersion}, Path={_updaterExePath}");
        return Version.Parse(versionInfo.FileVersion ?? "0.0.0.0");
    }

    private void LogVersionMetadata(string path, string label)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            Version? assemblyVersion = null;
            string informationalVersion = "<none>";

            try
            {
                assemblyVersion = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (Exception ex)
            {
                LogDiag($"{label} assembly version unavailable. Path={path}, Error={ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                var asm = Assembly.LoadFrom(path);
                informationalVersion = asm
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), inherit: false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion ?? "<none>";
            }
            catch (Exception ex)
            {
                LogDiag($"{label} informational version unavailable. Path={path}, Error={ex.GetType().Name}: {ex.Message}");
            }

            LogDiag($"{label} version metadata. Path={path}, FileVersion={versionInfo.FileVersion}, ProductVersion={versionInfo.ProductVersion}, AssemblyVersion={assemblyVersion?.ToString() ?? "<null>"}, InformationalVersion={informationalVersion}");
        }
        catch (Exception ex)
        {
            LogDiag($"{label} version metadata read failed. Path={path}, Error={ex.GetType().Name}: {ex.Message}");
        }
    }
}
