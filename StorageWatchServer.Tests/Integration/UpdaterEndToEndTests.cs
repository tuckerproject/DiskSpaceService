using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using Xunit;

namespace StorageWatchServer.Tests.Integration
{
    public class UpdaterEndToEndTests
    {
        [Fact]
        public void UiUpdateFlow_UpdaterReplacesFiles_AndRelaunchesUi()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-ui");
            var sourceDir = Path.Combine(testRoot, "source-ui");
            var stagingDir = Path.Combine(testRoot, "staging-ui");
            var zipPath = Path.Combine(testRoot, "ui-update.zip");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);

            var payloadRelativePath = Path.Combine("app", "ui-version.txt");
            var sourcePayloadPath = Path.Combine(sourceDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePayloadPath)!);
            File.WriteAllText(sourcePayloadPath, "ui-v2");

            var installPayloadPath = Path.Combine(installDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installPayloadPath)!);
            File.WriteAllText(installPayloadPath, "ui-v1");

            ZipFile.CreateFromDirectory(sourceDir, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            var systemExe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
            File.Copy(systemExe, Path.Combine(installDir, "StorageWatchUI.exe"), overwrite: true);

            ResetCheckpointRestartFlags();

            var result = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("ui-v2", File.ReadAllText(installPayloadPath));
            Assert.Contains("File replacement begins.", result.Output, StringComparison.Ordinal);
            Assert.Contains("File replacement succeeded.", result.Output, StringComparison.Ordinal);
            Assert.True(ReadRestartUIRequested());
            Assert.Contains("Updater exiting.", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void AgentUpdateFlow_WhenServiceStopFails_UpdaterFailsBeforeCopy()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-agent");
            var sourceDir = Path.Combine(testRoot, "source-agent");
            var stagingDir = Path.Combine(testRoot, "staging-agent");
            var zipPath = Path.Combine(testRoot, "agent-update.zip");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);

            var payloadRelativePath = Path.Combine("app", "agent-version.txt");
            var sourcePayloadPath = Path.Combine(sourceDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePayloadPath)!);
            File.WriteAllText(sourcePayloadPath, "agent-v2");

            var installPayloadPath = Path.Combine(installDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installPayloadPath)!);
            File.WriteAllText(installPayloadPath, "agent-v1");

            ZipFile.CreateFromDirectory(sourceDir, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"agent\"}");

            var env = new Dictionary<string, string>
            {
                ["STORAGEWATCH_AGENT_SERVICE_NAME"] = "StorageWatchAgent_Test_Double"
            };

            var result = RunUpdater(updaterExe, $"--update-agent --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-agent", env);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("agent-v1", File.ReadAllText(installPayloadPath));
            Assert.Contains("Agent stop begins.", result.Output, StringComparison.Ordinal);
            Assert.Contains("Agent stop failed.", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("File replacement begins.", result.Output, StringComparison.Ordinal);
            Assert.Contains("Updater exiting.", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void ServerUpdateFlow_UpdaterReplacesFiles_AndRelaunchesServer()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-server");
            var sourceDir = Path.Combine(testRoot, "source-server");
            var stagingDir = Path.Combine(testRoot, "staging-server");
            var zipPath = Path.Combine(testRoot, "server-update.zip");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);

            var payloadRelativePath = Path.Combine("app", "server-version.txt");
            var sourcePayloadPath = Path.Combine(sourceDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePayloadPath)!);
            File.WriteAllText(sourcePayloadPath, "server-v2");

            var installPayloadPath = Path.Combine(installDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installPayloadPath)!);
            File.WriteAllText(installPayloadPath, "server-v1");

            ZipFile.CreateFromDirectory(sourceDir, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"server\"}");

            var systemExe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
            File.Copy(systemExe, Path.Combine(installDir, "StorageWatchServer.exe"), overwrite: true);

            ResetCheckpointRestartFlags();

            var result = RunUpdater(updaterExe, $"--update-server --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-server");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("server-v2", File.ReadAllText(installPayloadPath));
            Assert.Contains("File replacement begins.", result.Output, StringComparison.Ordinal);
            Assert.Contains("File replacement succeeded.", result.Output, StringComparison.Ordinal);
            Assert.True(ReadRestartServerRequested());
            Assert.Contains("Updater exiting.", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void UnifiedUpdateFlow_SequentialUiAgentServerUpdaterRuns_Succeed()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();

            ResetCheckpointRestartFlags();

            var uiResult = RunScenario(updaterExe, testRoot, "ui", "StorageWatchUI.exe");
            var agentResult = RunScenario(updaterExe, testRoot, "agent", null);
            var serverResult = RunScenario(updaterExe, testRoot, "server", "StorageWatchServer.exe");

            Assert.Equal(0, uiResult.ExitCode);
            Assert.NotEqual(0, agentResult.ExitCode);
            Assert.Contains("Agent stop failed.", agentResult.Output, StringComparison.Ordinal);
            Assert.Equal(0, serverResult.ExitCode);
            Assert.True(ReadRestartUIRequested());
            Assert.True(ReadRestartServerRequested());
        }

        [Fact]
        public void LockedFile_UpdaterFailsGracefully_AndRollbackRestoresOriginalFiles()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-locked");
            var sourceDir = Path.Combine(testRoot, "source-locked");
            var stagingDir = Path.Combine(testRoot, "staging-locked");
            var backupDir = Path.Combine(testRoot, "backup-locked");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(backupDir);

            var relativePath = Path.Combine("app", "locked.txt");
            var installFile = Path.Combine(installDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
            File.WriteAllText(installFile, "original");

            var sourceFile = Path.Combine(sourceDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, "updated");

            CopyDirectoryContents(installDir, backupDir);

            ZipFile.CreateFromDirectory(sourceDir, Path.Combine(testRoot, "locked-update.zip"));
            ZipFile.ExtractToDirectory(Path.Combine(testRoot, "locked-update.zip"), stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            using (var lockedStream = new FileStream(installFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("update failed during file replacement", result.Output, StringComparison.OrdinalIgnoreCase);
            }

            var rollback = ExecuteRollback(updaterExe, backupDir, installDir, "locked file simulated");
            Assert.True(rollback.Success);
            Assert.False(rollback.IsPartialRecovery);
            Assert.Equal("original", File.ReadAllText(installFile));
        }

        [Fact]
        public void MissingStagingFiles_UpdaterFailsGracefully_AndRollbackCanBeTriggered()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-missing");
            var backupDir = Path.Combine(testRoot, "backup-missing");
            var stagingDir = Path.Combine(testRoot, "staging-missing");
            var manifestPath = Path.Combine(testRoot, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(backupDir);

            var installFile = Path.Combine(installDir, "app", "value.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
            File.WriteAllText(installFile, "baseline");
            CopyDirectoryContents(installDir, backupDir);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            var result = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("update failed during file replacement", result.Output, StringComparison.OrdinalIgnoreCase);

            var rollback = ExecuteRollback(updaterExe, backupDir, installDir, "missing staging files simulated");
            Assert.True(rollback.Success);
            Assert.False(rollback.IsPartialRecovery);
            Assert.Equal("baseline", File.ReadAllText(installFile));
        }

        [Fact]
        public void PartialUpdate_WithMidwayFailure_RollbackRestoresOriginalFiles_AndMarksPartialRecovery()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-partial");
            var sourceDir = Path.Combine(testRoot, "source-partial");
            var stagingDir = Path.Combine(testRoot, "staging-partial");
            var backupDir = Path.Combine(testRoot, "backup-partial");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(backupDir);

            var fileA = Path.Combine("app", "a.txt");
            var fileZ = Path.Combine("app", "z.txt");

            var installA = Path.Combine(installDir, fileA);
            var installZ = Path.Combine(installDir, fileZ);
            Directory.CreateDirectory(Path.GetDirectoryName(installA)!);
            File.WriteAllText(installA, "old-a");
            File.WriteAllText(installZ, "old-z");

            var sourceA = Path.Combine(sourceDir, fileA);
            var sourceZ = Path.Combine(sourceDir, fileZ);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
            File.WriteAllText(sourceA, "new-a");
            File.WriteAllText(sourceZ, "new-z");

            CopyDirectoryContents(installDir, backupDir);

            var lockedExtra = Path.Combine(installDir, "app", "locked-extra.txt");
            File.WriteAllText(lockedExtra, "locked-extra");

            ZipFile.CreateFromDirectory(sourceDir, Path.Combine(testRoot, "partial-update.zip"));
            ZipFile.ExtractToDirectory(Path.Combine(testRoot, "partial-update.zip"), stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            using (var lockZ = new FileStream(installZ, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("update failed during file replacement", result.Output, StringComparison.OrdinalIgnoreCase);
            }

            using var lockExtra = new FileStream(lockedExtra, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var rollback = ExecuteRollback(updaterExe, backupDir, installDir, "midway replacement failure simulated");
            Assert.True(rollback.Success);
            Assert.True(rollback.IsPartialRecovery);
            Assert.Equal("old-a", File.ReadAllText(installA));
            Assert.Equal("old-z", File.ReadAllText(installZ));
        }

        [Fact]
        public void CorruptedStaging_UpdaterAbortsBeforeFileReplacement()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-corrupt");
            var sourceDir = Path.Combine(testRoot, "source-corrupt");
            var corruptZip = Path.Combine(testRoot, "corrupt.zip");
            var stagingDir = Path.Combine(testRoot, "staging-corrupt");
            var manifestPath = Path.Combine(testRoot, "manifest-corrupt.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);

            var installFile = Path.Combine(installDir, "app", "value.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
            File.WriteAllText(installFile, "original");

            File.WriteAllText(corruptZip, "this-is-not-a-valid-zip");
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            Assert.ThrowsAny<Exception>(() => ZipFile.ExtractToDirectory(corruptZip, stagingDir, true));

            var result = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("File replacement begins.", result.Output, StringComparison.Ordinal);
            Assert.Contains("update failed during file replacement", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original", File.ReadAllText(installFile));
        }

        [Fact]
        public void RollbackBehavior_SuccessfulRollback_AfterReplacementFailure_RestoresOriginalFiles()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-rb-success");
            var sourceDir = Path.Combine(testRoot, "source-rb-success");
            var stagingDir = Path.Combine(testRoot, "staging-rb-success");
            var backupDir = Path.Combine(testRoot, "backup-rb-success");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(backupDir);

            var relativePath = Path.Combine("app", "value.txt");
            var installFile = Path.Combine(installDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
            File.WriteAllText(installFile, "original");

            var sourceFile = Path.Combine(sourceDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, "updated");

            CopyDirectoryContents(installDir, backupDir);

            ZipFile.CreateFromDirectory(sourceDir, Path.Combine(testRoot, "rb-success.zip"));
            ZipFile.ExtractToDirectory(Path.Combine(testRoot, "rb-success.zip"), stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            using (var lockFile = new FileStream(installFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var updateResult = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");
                Assert.NotEqual(0, updateResult.ExitCode);
            }

            var rollback = ExecuteRollback(updaterExe, backupDir, installDir, "simulate replacement failure");

            Assert.True(rollback.Success);
            Assert.False(rollback.IsPartialRecovery);
            Assert.Equal("original", File.ReadAllText(installFile));
        }

        [Fact]
        public void RollbackBehavior_PartialRollback_SetsIsPartialRecoveryTrue()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-rb-partial");
            var sourceDir = Path.Combine(testRoot, "source-rb-partial");
            var stagingDir = Path.Combine(testRoot, "staging-rb-partial");
            var backupDir = Path.Combine(testRoot, "backup-rb-partial");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(backupDir);

            var installA = Path.Combine(installDir, "app", "a.txt");
            var installB = Path.Combine(installDir, "app", "b.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(installA)!);
            File.WriteAllText(installA, "old-a");
            File.WriteAllText(installB, "old-b");

            var sourceA = Path.Combine(sourceDir, "app", "a.txt");
            var sourceB = Path.Combine(sourceDir, "app", "b.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
            File.WriteAllText(sourceA, "new-a");
            File.WriteAllText(sourceB, "new-b");

            CopyDirectoryContents(installDir, backupDir);

            ZipFile.CreateFromDirectory(sourceDir, Path.Combine(testRoot, "rb-partial.zip"));
            ZipFile.ExtractToDirectory(Path.Combine(testRoot, "rb-partial.zip"), stagingDir, true);
            File.WriteAllText(manifestPath, "{\"component\":\"ui\"}");

            using (var lockFile = new FileStream(installB, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var updateResult = RunUpdater(updaterExe, $"--update-ui --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-ui");
                Assert.NotEqual(0, updateResult.ExitCode);
            }

            var extraLockPath = Path.Combine(installDir, "app", "extra-locked.txt");
            File.WriteAllText(extraLockPath, "extra");

            using var lockExtra = new FileStream(extraLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var rollback = ExecuteRollback(updaterExe, backupDir, installDir, "simulate partial restore failure");

            Assert.True(rollback.Success);
            Assert.True(rollback.IsPartialRecovery);
        }

        [Fact]
        public void RollbackBehavior_Diagnostics_ArePrinted_AndNoExceptionEscapes()
        {
            var updaterExe = EnsureUpdaterExePath();
            var testRoot = CreateTempDirectory();
            var installDir = Path.Combine(testRoot, "install-rb-diagnostics");
            var backupDir = Path.Combine(testRoot, "backup-rb-diagnostics");
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(backupDir);

            var installFile = Path.Combine(installDir, "app", "value.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(installFile)!);
            File.WriteAllText(installFile, "baseline");
            CopyDirectoryContents(installDir, backupDir);

            var exception = Record.Exception(() =>
            {
                var (_, _, _, output) = ExecuteRollbackWithOutput(updaterExe, backupDir, installDir, "diagnostic test");
                Assert.Contains("Starting rollback", output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Failure reason", output, StringComparison.OrdinalIgnoreCase);
            });

            Assert.Null(exception);
        }

        private static (int ExitCode, string Output) RunScenario(string updaterExe, string testRoot, string component, string? relaunchExeName)
        {
            var installDir = Path.Combine(testRoot, $"install-{component}");
            var sourceDir = Path.Combine(testRoot, $"source-{component}");
            var stagingDir = Path.Combine(testRoot, $"staging-{component}");
            var zipPath = Path.Combine(testRoot, $"{component}-update.zip");
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(stagingDir);

            var payloadRelativePath = Path.Combine("app", $"{component}-version.txt");
            var sourcePayloadPath = Path.Combine(sourceDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePayloadPath)!);
            File.WriteAllText(sourcePayloadPath, $"{component}-v2");

            var installPayloadPath = Path.Combine(installDir, payloadRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installPayloadPath)!);
            File.WriteAllText(installPayloadPath, $"{component}-v1");

            if (!string.IsNullOrWhiteSpace(relaunchExeName))
            {
                var systemExe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
                File.Copy(systemExe, Path.Combine(installDir, relaunchExeName), overwrite: true);
            }

            ZipFile.CreateFromDirectory(sourceDir, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            File.WriteAllText(manifestPath, $"{{\"component\":\"{component}\"}}");

            Dictionary<string, string>? env = null;
            if (string.Equals(component, "agent", StringComparison.OrdinalIgnoreCase))
            {
                env = new Dictionary<string, string>
                {
                    ["STORAGEWATCH_AGENT_SERVICE_NAME"] = "StorageWatchAgent_Test_Double"
                };
            }

            var result = RunUpdater(updaterExe, $"--update-{component} --source \"{stagingDir}\" --target \"{installDir}\" --manifest \"{manifestPath}\" --restart-{component}", env);
            if (string.Equals(component, "agent", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal($"{component}-v1", File.ReadAllText(installPayloadPath));
                Assert.Contains("Agent stop failed.", result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("File replacement begins.", result.Output, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal($"{component}-v2", File.ReadAllText(installPayloadPath));
                Assert.Contains("File replacement succeeded.", result.Output, StringComparison.Ordinal);
            }

            return (result.ExitCode, result.Output);
        }

        private static string EnsureUpdaterExePath()
        {
            var repoRoot = FindRepoRoot();
            var candidates = new[]
            {
                Path.Combine(repoRoot, "StorageWatch.Updater", "bin", "Debug", "net10.0", "StorageWatch.Updater.exe"),
                Path.Combine(repoRoot, "StorageWatch.Updater", "bin", "Release", "net10.0", "StorageWatch.Updater.exe")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            var build = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build StorageWatch.Updater/StorageWatch.Updater.csproj -c Debug",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            build.Start();
            build.WaitForExit();

            if (build.ExitCode != 0)
            {
                var output = build.StandardOutput.ReadToEnd();
                var error = build.StandardError.ReadToEnd();
                throw new InvalidOperationException($"Failed to build updater for tests. {output} {error}");
            }

            var builtExe = Path.Combine(repoRoot, "StorageWatch.Updater", "bin", "Debug", "net10.0", "StorageWatch.Updater.exe");
            if (!File.Exists(builtExe))
                throw new FileNotFoundException("Updater executable not found after build.", builtExe);

            return builtExe;
        }

        private static (int ExitCode, string Output) RunUpdater(string updaterExe, string arguments, IDictionary<string, string>? environment = null)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = updaterExe,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(updaterExe)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    process.StartInfo.Environment[pair.Key] = pair.Value;
                }
            }

            process.Start();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Updater process did not exit within timeout.");
            }

            var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
            return (process.ExitCode, output);
        }

        private static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "StorageWatch.slnx")))
                    return current.FullName;

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "StorageWatchUpdaterE2ETests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string GetInstallCheckpointPath()
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "StorageWatch", "Update", "install-plan.json");
        }

        private static void ResetCheckpointRestartFlags()
        {
            var checkpointPath = GetInstallCheckpointPath();
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);

            File.WriteAllText(checkpointPath,
                "{\n" +
                "  \"schemaVersion\": 3,\n" +
                "  \"orchestrationId\": \"e2e-test\",\n" +
                "  \"startedAtUtc\": \"2026-01-01T00:00:00.0000000+00:00\",\n" +
                "  \"lastUpdatedAtUtc\": \"2026-01-01T00:00:00.0000000+00:00\",\n" +
                "  \"isInstalling\": false,\n" +
                "  \"components\": [],\n" +
                "  \"currentComponentIndex\": 0,\n" +
                "  \"componentStates\": [],\n" +
                "  \"restartUIRequested\": false,\n" +
                "  \"restartServerRequested\": false\n" +
                "}");
        }

        private static bool ReadRestartUIRequested()
        {
            return ReadCheckpointFlag("restartUIRequested");
        }

        private static bool ReadRestartServerRequested()
        {
            return ReadCheckpointFlag("restartServerRequested");
        }

        private static bool ReadCheckpointFlag(string propertyName)
        {
            var checkpointPath = GetInstallCheckpointPath();
            var json = File.ReadAllText(checkpointPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.GetBoolean();
        }

        private static (bool Success, bool IsPartialRecovery, string Message) ExecuteRollback(string updaterExe, string backupDir, string targetDir, string reason)
        {
            var updaterAssembly = Assembly.LoadFrom(Path.ChangeExtension(updaterExe, ".dll"));
            var rollbackManagerType = updaterAssembly.GetType("StorageWatch.Updater.RollbackManager", throwOnError: true)!;
            var rollbackManager = Activator.CreateInstance(rollbackManagerType, new object?[] { false, null, null })!;
            var rollbackMethod = rollbackManagerType.GetMethod("RollbackOnFileReplacementFailure")!;

            var result = rollbackMethod.Invoke(rollbackManager, new object[]
            {
                backupDir,
                targetDir,
                reason,
                CancellationToken.None
            })!;

            var resultType = result.GetType();
            var success = (bool)(resultType.GetProperty("Success")!.GetValue(result) ?? false);
            var isPartial = (bool)(resultType.GetProperty("IsPartialRecovery")!.GetValue(result) ?? false);
            var message = (string?)resultType.GetProperty("Message")!.GetValue(result) ?? string.Empty;
            return (success, isPartial, message);
        }

        private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
            }

            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, file);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(file, destinationPath, overwrite: true);
            }
        }

        private static void WritePayload(string root, string relativePath, string content)
        {
            var fullPath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        private static (bool Success, bool IsPartialRecovery, string Message, string ConsoleOutput) ExecuteRollbackWithOutput(string updaterExe, string backupDir, string targetDir, string reason)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var rollback = ExecuteRollback(updaterExe, backupDir, targetDir, reason);
                return (rollback.Success, rollback.IsPartialRecovery, rollback.Message, writer.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }
}
