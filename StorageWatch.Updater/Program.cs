using StorageWatch.Updater;
using StorageWatch.Updater.Services.Logging;
using StorageWatch.Shared.Update.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

// Initialize logging at the very start
var logFilePath = LogDirectoryInitializer.GetLogFilePath("updater.log");
var logger = new RollingFileLogger(logFilePath);

logger.Log("[STARTUP] StorageWatch Updater starting...");
logger.Log($"[STARTUP] Raw arguments: {string.Join(" ", args)}");
logger.Log($"[DIAG] ProcessPath: {Environment.ProcessPath}");
logger.Log($"[DIAG] BaseDirectory: {AppContext.BaseDirectory}");
logger.Log($"[DIAG] CurrentDirectory: {Environment.CurrentDirectory}");

var updatedCount = 0;
var skippedCount = 0;

void LogComplete()
{
    logger.Log($"[COMPLETE] Updater finished. Updated={updatedCount}, Skipped={skippedCount}");
}

bool TryPersistRestartIntent(bool restartUiRequested, bool restartServerRequested)
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var checkpointPath = Path.Combine(programData, "StorageWatch", "Update", "install-plan.json");
    return CheckpointHandoffStore.TryPersistRestartIntent(checkpointPath, restartUiRequested, restartServerRequested, logger.Log);
}

bool TryPersistAgentHandoffComplete()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var checkpointPath = Path.Combine(programData, "StorageWatch", "Update", "install-plan.json");
    return CheckpointHandoffStore.TryPersistAgentHandoffComplete(checkpointPath, logger.Log);
}

try
{
    ArgumentParser.SetDiagnosticLogger(logger.Log);
    FileReplacementEngine.SetDiagnosticLogger(logger.Log);
    var arguments = ArgumentParser.Parse(args);

    logger.Log($"[PARSED] UpdateUI: {arguments.UpdateUI}");
    logger.Log($"[PARSED] UpdateAgent: {arguments.UpdateAgent}");
    logger.Log($"[PARSED] UpdateServer: {arguments.UpdateServer}");
    logger.Log($"[PARSED] SelfUpdateStage: {arguments.SelfUpdateStage}");
    logger.Log($"[PARSED] SelfUpdateApply: {arguments.SelfUpdateApply}");
    logger.Log($"[PARSED] RestartUI: {arguments.RestartUI}");
    logger.Log($"[PARSED] RestartAgent: {arguments.RestartAgent}");
    logger.Log($"[PARSED] RestartServer: {arguments.RestartServer}");
    logger.Log($"[PARSED] AllowSystemRestartIntent: {arguments.AllowSystemRestartIntent}");
    logger.Log($"[PARSED] ManifestPath: {arguments.ManifestPath}");
    logger.Log($"[PARSED] SourcePath: {arguments.SourcePath}");
    logger.Log($"[PARSED] TargetPath: {arguments.TargetPath}");
    logger.Log($"[PARSED] SelfUpdateStagingPath: {arguments.SelfUpdateStagingPath}");

    var selfUpdateManager = new SelfUpdateManager(diagnosticLogger: logger.Log);

    if (arguments.SelfUpdateStage)
    {
        logger.Log("[DIAG] Entering branch: --self-update-stage");
        if (string.IsNullOrWhiteSpace(arguments.ManifestPath) || !File.Exists(arguments.ManifestPath))
        {
            logger.Log("[ERROR] --self-update-stage requires --manifest path to an existing manifest file.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Error: --self-update-stage requires --manifest path to an existing manifest file.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        logger.Log("[SELF-UPDATE] Running explicit self-update stage mode.");
        var selfUpdateStaged = await selfUpdateManager.RunSelfUpdateStageAsync(arguments.ManifestPath, arguments);
        if (selfUpdateStaged)
        {
            updatedCount++;
            logger.Log("[SELF-UPDATE] Self-update handoff complete; exiting old updater process to release file locks.");
            logger.Log("[SELF-UPDATE] Stage mode completed.");
            LogComplete();
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.Success);
        }
        else
        {
            skippedCount++;
            logger.Log("[DIAG] Self-update stage determined updater is not newer; apply was not launched.");
        }
        logger.Log("[SELF-UPDATE] Stage mode completed.");
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    if (arguments.SelfUpdateApply)
    {
        logger.Log("[DIAG] Entering branch: --self-update-apply");
        if (string.IsNullOrWhiteSpace(arguments.SelfUpdateStagingPath) || string.IsNullOrWhiteSpace(arguments.TargetPath))
        {
            logger.Log("[ERROR] --self-update-apply requires --self-update-staging and --target.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Error: --self-update-apply requires --self-update-staging and --target.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        logger.Log("[SELF-UPDATE] Running explicit self-update apply mode.");
        await selfUpdateManager.RunSelfUpdateApplyAsync(arguments);
        updatedCount++;
        logger.Log("[SELF-UPDATE] Apply mode completed.");
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    // Check for updater self-update (if manifest path provided)
    if (!string.IsNullOrWhiteSpace(arguments.ManifestPath) && File.Exists(arguments.ManifestPath))
    {
        try
        {
            logger.Log("[SELF-UPDATE] Checking for updater self-update...");
            var manifestJson = await File.ReadAllTextAsync(arguments.ManifestPath);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                manifestJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (manifest?.Updater != null)
            {
                if (selfUpdateManager.IsUpdateAvailable(manifest.Updater))
                {
                    logger.Log("[SELF-UPDATE] Updater update available. Initiating self-update...");
                    var selfUpdateStaged = await selfUpdateManager.RunLegacySelfUpdateStageAsync(manifest.Updater, arguments);
                    if (selfUpdateStaged)
                    {
                        updatedCount++;
                        logger.Log("[SELF-UPDATE] Self-update handoff complete; exiting old updater process to release file locks.");
                        LogComplete();
                        Console.WriteLine("Updater exiting.");
                        Environment.Exit(ExitCodes.Success);
                    }
                }
                else
                {
                    logger.Log("[SELF-UPDATE] Updater is already up to date.");
                    skippedCount++;
                }
            }
            else
            {
                logger.Log("[DIAG] Manifest updater entry missing; self-update check skipped.");
                skippedCount++;
            }
        }
        catch (Exception ex)
        {
            logger.Log($"[SELF-UPDATE] Failed to check/apply updater self-update: {ex.Message}");
            logger.Log("[DIAG] Self-update check failed; continuing with component update flow.");
            skippedCount++;
            // Continue with component update even if self-update fails
        }
    }
    else
    {
        logger.Log("[DIAG] Manifest path missing or file not found; manifest-based self-update check skipped.");
        skippedCount++;
    }

    if (arguments.UpdateUI)
    {
        logger.Log("[DIAG] Entering branch: --update-ui");
        if (string.IsNullOrWhiteSpace(arguments.SourcePath) || string.IsNullOrWhiteSpace(arguments.TargetPath))
        {
            logger.Log("[ERROR] --update-ui requires both --source and --target paths.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Error: --update-ui requires both --source and --target paths.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        var fileReplacementEngine = new FileReplacementEngine();

        logger.Log("[STEP] File replacement begins for UI.");
        Console.WriteLine("File replacement begins.");
        var replaced = fileReplacementEngine.TryCopyFilesFromStaging(arguments.SourcePath, arguments.TargetPath);

        if (!replaced)
        {
            logger.Log("[ERROR] UI update failed during file replacement.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("UI update failed during file replacement.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log("[STEP] File replacement succeeded for UI.");
        Console.WriteLine("File replacement succeeded.");

        if (arguments.RestartUI)
        {
            logger.Log("[UI-RESTART] Restart requested after UI update; direct process launch is suppressed in updater.");
            logger.Log($"[UI-RESTART] Suppression context: User={Environment.UserName}, IsWindows={OperatingSystem.IsWindows()}");
            if (!TryPersistRestartIntent(restartUiRequested: true, restartServerRequested: false))
            {
                logger.Log("[WARN] UI restart intent could not be recorded to checkpoint.");
            }
        }
        else
        {
            logger.Log("[DIAG] UI restart not requested for this updater invocation.");
        }

        logger.Log("[SUCCESS] UI update completed successfully.");
        updatedCount++;
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    if (arguments.UpdateAgent)
    {
        logger.Log("[DIAG] Entering branch: --update-agent");
        if (string.IsNullOrWhiteSpace(arguments.SourcePath) || string.IsNullOrWhiteSpace(arguments.TargetPath))
        {
            logger.Log("[ERROR] --update-agent requires both --source and --target paths.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Error: --update-agent requires both --source and --target paths.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        var fileReplacementEngine = new FileReplacementEngine();

        var serviceName = Environment.GetEnvironmentVariable("STORAGEWATCH_AGENT_SERVICE_NAME");
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = "StorageWatchAgent";

        var agentServiceHelper = new AgentRestartHelper(logger.Log);

        logger.Log($"[STEP] Agent stop begins for service: {serviceName}");
        Console.WriteLine("Agent stop begins.");
        if (!agentServiceHelper.TryStopAgentService(serviceName))
        {
            logger.Log("[ERROR] Agent stop failed before file replacement.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Agent stop failed.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log("[STEP] Agent stop completed.");

        logger.Log("[STEP] File replacement begins for Agent.");
        Console.WriteLine("File replacement begins.");
        var replaced = false;
        var retryDelay = TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            replaced = fileReplacementEngine.TryCopyFilesFromStaging(arguments.SourcePath, arguments.TargetPath);
            if (replaced)
                break;

            logger.Log($"[STEP] Agent file replacement attempt {attempt} failed.");
            if (attempt < 3)
            {
                logger.Log($"[STEP] Retrying Agent file replacement in {retryDelay.TotalMilliseconds}ms.");
                await Task.Delay(retryDelay);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2);
            }
        }

        if (!replaced)
        {
            logger.Log("[ERROR] Agent update failed during file replacement.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Agent update failed during file replacement.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log("[STEP] File replacement succeeded for Agent.");
        Console.WriteLine("File replacement succeeded.");

        if (!TryPersistAgentHandoffComplete())
        {
            logger.Log("[ERROR] Agent update finished but handoff-complete marker could not be persisted.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Agent handoff completion marker write failed.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log($"[STEP] Agent start begins for service: {serviceName}");
        Console.WriteLine("Agent start begins.");
        if (!agentServiceHelper.TryStartAgentService(serviceName))
        {
            logger.Log("[ERROR] Agent start failed after file replacement and handoff-complete checkpoint persistence.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Agent start failed.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log("[STEP] Agent start completed.");

        logger.Log("[SUCCESS] Agent update completed successfully.");
        updatedCount++;
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    if (arguments.UpdateServer)
    {
        logger.Log("[DIAG] Entering branch: --update-server");
        if (string.IsNullOrWhiteSpace(arguments.SourcePath) || string.IsNullOrWhiteSpace(arguments.TargetPath))
        {
            logger.Log("[ERROR] --update-server requires both --source and --target paths.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Error: --update-server requires both --source and --target paths.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        var fileReplacementEngine = new FileReplacementEngine();

        logger.Log("[STEP] File replacement begins for Server.");
        Console.WriteLine("File replacement begins.");
        var replaced = fileReplacementEngine.TryCopyFilesFromStaging(arguments.SourcePath, arguments.TargetPath);

        if (!replaced)
        {
            logger.Log("[ERROR] Server update failed during file replacement.");
            skippedCount++;
            LogComplete();
            Console.WriteLine("Server update failed during file replacement.");
            Console.WriteLine("Updater exiting.");
            Environment.Exit(ExitCodes.UnexpectedError);
        }

        logger.Log("[STEP] File replacement succeeded for Server.");
        Console.WriteLine("File replacement succeeded.");

        if (arguments.RestartServer)
        {
            logger.Log("[SERVER-RESTART] Restart requested after server update; direct process launch is suppressed in updater.");
            logger.Log($"[SERVER-RESTART] Suppression context: User={Environment.UserName}, IsWindows={OperatingSystem.IsWindows()}");
            if (!TryPersistRestartIntent(restartUiRequested: false, restartServerRequested: true))
            {
                logger.Log("[WARN] Server restart intent could not be recorded to checkpoint.");
            }
        }
        else
        {
            logger.Log("[DIAG] Server restart not requested for this updater invocation.");
        }

        logger.Log("[SUCCESS] Server update completed successfully.");
        updatedCount++;
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    if (arguments.RestartAgent)
    {
        var serviceName = Environment.GetEnvironmentVariable("STORAGEWATCH_AGENT_SERVICE_NAME");
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = "StorageWatchAgent";

        logger.Log($"[STEP] Agent restart begins for service: {serviceName}");
        Console.WriteLine("Agent restart begins.");
        var agentRestartHelper = new AgentRestartHelper(logger.Log);
        agentRestartHelper.TryRestartAgentService(serviceName);
        logger.Log("[STEP] Agent restart completed.");

        logger.Log("[SUCCESS] Agent restart completed successfully.");
        updatedCount++;
        LogComplete();
        Console.WriteLine("Updater exiting.");
        Environment.Exit(ExitCodes.Success);
    }

    logger.Log("[SUCCESS] Updater completed without any update or restart actions.");
    skippedCount++;
    LogComplete();
    Console.WriteLine("Updater exiting.");
    Environment.Exit(ExitCodes.Success);
}
catch (Exception ex)
{
    logger.Log($"[ERROR] Unhandled exception: {ex.GetType().FullName}: {ex.Message}");
    logger.Log($"[ERROR] Stack trace: {ex.StackTrace}");
    if (ex.InnerException != null)
    {
        logger.Log($"[ERROR] Inner exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
    }
    skippedCount++;
    LogComplete();
    Console.WriteLine("Updater exiting.");
    Environment.Exit(ExitCodes.UnexpectedError);
}
