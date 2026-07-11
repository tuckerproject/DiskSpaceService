using System.Security.Principal;

namespace StorageWatch.Updater;

/// <summary>
/// Represents the parsed command-line arguments for the updater.
/// </summary>
internal class UpdaterArguments
{
    public bool SelfUpdateStage { get; set; }
    public bool SelfUpdateApply { get; set; }
    public bool UpdateUI { get; set; }
    public bool UpdateAgent { get; set; }
    public bool UpdateServer { get; set; }
    public bool RestartUI { get; set; }
    public bool RestartAgent { get; set; }
    public bool RestartServer { get; set; }
    public bool AllowSystemRestartIntent { get; set; }
    public string? ManifestPath { get; set; }
    public string? SourcePath { get; set; }
    public string? TargetPath { get; set; }
    public string? SelfUpdateStagingPath { get; set; }
    public string? ContinueArguments { get; set; }
    public int? WaitForPid { get; set; }
}

/// <summary>
/// Result of parsing command-line arguments.
/// </summary>
internal class ParseResult
{
    /// <summary>
    /// Gets whether parsing succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets the parsed arguments if parsing succeeded.
    /// </summary>
    public UpdaterArguments Arguments { get; set; }

    /// <summary>
    /// Gets validation errors if parsing failed.
    /// </summary>
    public List<string> Errors { get; set; }

    public ParseResult()
    {
        Arguments = new UpdaterArguments();
        Errors = new List<string>();
        Success = false;
    }
}

/// <summary>
/// Parses command-line arguments for the updater.
/// Designed for testability with instance methods and non-throwing variants.
/// </summary>
internal class ArgumentParser
{
    private static Action<string>? _diagnosticLogger;

    public static void SetDiagnosticLogger(Action<string>? diagnosticLogger)
    {
        _diagnosticLogger = diagnosticLogger;
    }

    private static void LogDiag(string message)
    {
        _diagnosticLogger?.Invoke($"[DIAG] {message}");
    }

    /// <summary>
    /// Initializes a new ArgumentParser instance.
    /// </summary>
    public ArgumentParser()
    {
    }

    /// <summary>
    /// Parses command-line arguments into an UpdaterArguments object.
    /// Returns results without exiting or throwing.
    /// </summary>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <returns>A ParseResult containing parsed values or errors.</returns>
    public ParseResult TryParse(string[] args)
    {
        LogDiag($"ArgumentParser.TryParse start. ArgCount={args.Length}");
        var result = new ParseResult();
        var arguments = new UpdaterArguments();
        var errors = new List<string>();

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                switch (arg)
                {
                    case "--self-update-stage":
                        arguments.SelfUpdateStage = true;
                        LogDiag("Flag parsed: --self-update-stage=true");
                        break;

                    case "--self-update-apply":
                        arguments.SelfUpdateApply = true;
                        LogDiag("Flag parsed: --self-update-apply=true");
                        break;

                    case "--update-ui":
                        arguments.UpdateUI = true;
                        LogDiag("Flag parsed: --update-ui=true");
                        break;

                    case "--update-agent":
                        arguments.UpdateAgent = true;
                        LogDiag("Flag parsed: --update-agent=true");
                        break;

                    case "--update-server":
                        arguments.UpdateServer = true;
                        LogDiag("Flag parsed: --update-server=true");
                        break;

                    case "--restart-ui":
                        arguments.RestartUI = true;
                        LogDiag("Flag parsed: --restart-ui=true");
                        break;

                    case "--restart-agent":
                        arguments.RestartAgent = true;
                        LogDiag("Flag parsed: --restart-agent=true");
                        break;

                    case "--restart-server":
                        arguments.RestartServer = true;
                        LogDiag("Flag parsed: --restart-server=true");
                        break;

                    case "--allow-system-restart-intent":
                        arguments.AllowSystemRestartIntent = true;
                        LogDiag("Flag parsed: --allow-system-restart-intent=true");
                        break;

                    case "--manifest":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            arguments.ManifestPath = args[++i];
                            LogDiag($"Value parsed: --manifest={arguments.ManifestPath}");
                        }
                        else
                        {
                            errors.Add("Error: --manifest flag requires a path argument.");
                            LogDiag("Validation failed: --manifest missing value");
                        }
                        break;

                    case "--self-update-staging":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            arguments.SelfUpdateStagingPath = args[++i];
                            LogDiag($"Value parsed: --self-update-staging={arguments.SelfUpdateStagingPath}");
                        }
                        else
                        {
                            errors.Add("Error: --self-update-staging flag requires a path argument.");
                            LogDiag("Validation failed: --self-update-staging missing value");
                        }
                        break;

                    case "--continue-args":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            arguments.ContinueArguments = args[++i];
                            LogDiag("Value parsed: --continue-args provided");
                        }
                        else
                        {
                            errors.Add("Error: --continue-args flag requires an argument string.");
                            LogDiag("Validation failed: --continue-args missing value");
                        }
                        break;

                    case "--wait-for-pid":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            var pidRaw = args[++i];
                            if (int.TryParse(pidRaw, out var pid) && pid > 0)
                            {
                                arguments.WaitForPid = pid;
                                LogDiag($"Value parsed: --wait-for-pid={arguments.WaitForPid}");
                            }
                            else
                            {
                                errors.Add("Error: --wait-for-pid flag requires a positive integer process id.");
                                LogDiag($"Validation failed: --wait-for-pid invalid value '{pidRaw}'");
                            }
                        }
                        else
                        {
                            errors.Add("Error: --wait-for-pid flag requires a process id argument.");
                            LogDiag("Validation failed: --wait-for-pid missing value");
                        }
                        break;

                    case "--source":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            arguments.SourcePath = args[++i];
                            LogDiag($"Value parsed: --source={arguments.SourcePath}");
                        }
                        else
                        {
                            errors.Add("Error: --source flag requires a path argument.");
                            LogDiag("Validation failed: --source missing value");
                        }
                        break;

                    case "--target":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        {
                            arguments.TargetPath = args[++i];
                            LogDiag($"Value parsed: --target={arguments.TargetPath}");
                        }
                        else
                        {
                            errors.Add("Error: --target flag requires a path argument.");
                            LogDiag("Validation failed: --target missing value");
                        }
                        break;

                    default:
                        errors.Add($"Error: Unknown argument '{arg}'.");
                        LogDiag($"Validation failed: unknown argument {arg}");
                        break;
                }
            }

            // Validate that at least one update or restart action is specified
            bool hasSelfUpdateAction = arguments.SelfUpdateStage || arguments.SelfUpdateApply;
            bool hasUpdateAction = arguments.UpdateUI || arguments.UpdateAgent || arguments.UpdateServer;
            bool hasRestartAction = arguments.RestartUI || arguments.RestartAgent || arguments.RestartServer;
            var componentUpdateFlagCount =
                (arguments.UpdateUI ? 1 : 0) +
                (arguments.UpdateAgent ? 1 : 0) +
                (arguments.UpdateServer ? 1 : 0);

            if (!hasSelfUpdateAction && !hasUpdateAction && !hasRestartAction)
            {
                errors.Add("Error: At least one update or restart action must be specified.");
                LogDiag("Validation failed: no action flags were provided");
            }

            if (arguments.SelfUpdateStage && arguments.SelfUpdateApply)
            {
                errors.Add("Error: --self-update-stage and --self-update-apply cannot be used together.");
                LogDiag("Validation failed: both --self-update-stage and --self-update-apply were set");
            }

            if (componentUpdateFlagCount > 1)
            {
                errors.Add("Error: Only one component update flag may be specified per updater run (--update-ui, --update-agent, or --update-server).");
                LogDiag($"Validation failed: multiple component update flags set (count={componentUpdateFlagCount})");
            }

            var runningAsSystem = IsRunningAsSystem();
            if ((arguments.RestartUI || arguments.RestartServer) && runningAsSystem && !arguments.AllowSystemRestartIntent)
            {
                errors.Add("Error: --restart-ui and --restart-server require --allow-system-restart-intent when updater runs as SYSTEM.");
                LogDiag("Validation failed: restart-ui/restart-server requested under SYSTEM without --allow-system-restart-intent");
            }

            // Set result
            if (errors.Count > 0)
            {
                result.Success = false;
                result.Errors = errors;
                LogDiag($"ArgumentParser.TryParse completed with errors. ErrorCount={errors.Count}");
            }
            else
            {
                result.Success = true;
                result.Arguments = arguments;
                LogDiag("ArgumentParser.TryParse completed successfully.");
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Exception during parsing: {ex.Message}");
            LogDiag($"ArgumentParser.TryParse exception: {ex.GetType().Name}: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Parses command-line arguments into an UpdaterArguments object.
    /// Validates required arguments and exits with non-zero code on failure.
    /// Static wrapper for backward compatibility.
    /// </summary>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <returns>An UpdaterArguments object containing the parsed values.</returns>
    public static UpdaterArguments Parse(string[] args)
    {
        var parser = new ArgumentParser();
        var result = parser.TryParse(args);

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                Console.WriteLine(error);
            }
            Console.WriteLine();
            PrintUsage();
            Environment.Exit(ExitCodes.InvalidArguments);
        }

        return result.Arguments;
    }

    /// <summary>
    /// Prints usage information to the console.
    /// </summary>
    public static void PrintUsage()
    {
        Console.WriteLine("StorageWatch Updater");
        Console.WriteLine("Usage: StorageWatch.Updater [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --self-update-stage      Stage and initiate updater self-update apply flow");
        Console.WriteLine("  --self-update-apply      Apply staged updater self-update to target folder");
        Console.WriteLine("  --update-ui              Update the UI component");
        Console.WriteLine("  --update-agent           Update the Agent component");
        Console.WriteLine("  --update-server          Update the Server component");
        Console.WriteLine("  --restart-ui             Restart the UI component after update");
        Console.WriteLine("  --restart-agent          Restart the Agent component after update");
        Console.WriteLine("  --restart-server         Restart the Server component after update");
        Console.WriteLine("  --allow-system-restart-intent Allow restart intent flags under SYSTEM for unified update flow");
        Console.WriteLine("  --manifest <path>        Path to the update manifest file");
        Console.WriteLine("  --source <path>          Path to the source directory containing update files");
        Console.WriteLine("  --target <path>          Path to the target installation directory");
        Console.WriteLine("  --self-update-staging <path> Path to extracted updater staging folder");
        Console.WriteLine("  --continue-args <text>   Serialized arguments for post-self-update continuation");
        Console.WriteLine("  --wait-for-pid <pid>     Wait for specified process id to exit before apply");
    }

    private static bool IsRunningAsSystem()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return false;

            var identity = WindowsIdentity.GetCurrent();
            return string.Equals(identity?.Name, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Environment.UserName, "SYSTEM", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
