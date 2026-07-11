using FluentAssertions;
using StorageWatch.Config.Options;
using StorageWatch.Shared.Update.Models;
using StorageWatch.Services.Alerting.Plugins;
using StorageWatch.Services.AutoUpdate;
using StorageWatch.Services.Logging;
using StorageWatchAgent.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate.Models;
using StorageWatch.Tests.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StorageWatch.Tests.UnitTests
{
    public class AutoUpdateTests
    {
        [Fact]
        public void ServiceUpdateChecker_ParseManifest_ParsesExpectedFields()
        {
            var json = "{\"version\":\"1.0.0\",\"service\":{\"version\":\"1.2.3\",\"downloadUrl\":\"https://example.com/update.zip\",\"sha256\":\"abc123\"},\"plugins\":[{\"name\":\"GroupMe\",\"version\":\"2.0.0\",\"downloadUrl\":\"https://example.com/plugin.zip\",\"sha256\":\"def\"}]}";
            var manifest = ServiceUpdateChecker.ParseManifest(json);
            manifest.Should().NotBeNull();
            manifest!.Agent.Should().NotBeNull();
            manifest.Agent.Version.Should().Be("1.2.3");
            manifest.Agent.DownloadUrl.Should().Be("https://example.com/update.zip");
            manifest.Agent.Sha256.Should().Be("abc123");
            manifest.Plugins.Should().HaveCount(1);
            manifest.Plugins[0].Id.Should().Be("GroupMe");
        }

        [Fact]
        public async Task ServiceUpdateChecker_CheckForUpdateAsync_ReturnsUpdateWhenNewerVersion()
        {
            var currentVersion = typeof(ServiceUpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            var newerVersion = new Version(currentVersion.Major + 1, 0, 0, 0);
            var manifestJson = $"{{\"version\":\"1.0.0\",\"service\":{{\"version\":\"{newerVersion}\",\"downloadUrl\":\"https://example.com/update.zip\",\"sha256\":\"abc\"}}}}";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestJson)
            });
            var httpClient = new HttpClient(handler);
            var optionsMonitor = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions
            {
                Enabled = true,
                ManifestUrl = "https://example.com/manifest.json",
                CheckIntervalMinutes = 60
            });
            var logger = new TestLogger<ServiceUpdateChecker>();
            var checker = new ServiceUpdateChecker(httpClient, logger, optionsMonitor);

            var result = await checker.CheckForUpdateAsync(CancellationToken.None);

            result.IsUpdateAvailable.Should().BeTrue();
            result.Component.Should().NotBeNull();
            result.Component!.Version.Should().Be(newerVersion.ToString());
        }

        [Fact]
        public void ServiceUpdateChecker_IsUpdateAvailable_ReturnsTrueForNewerVersion()
        {
            var current = new Version(1, 0, 0);
            var manifest = new Version(1, 1, 0);

            var result = ServiceUpdateChecker.IsUpdateAvailable(current, manifest);

            result.Should().BeTrue();
        }

        [Fact]
        public async Task ServiceUpdateDownloader_DownloadsAndValidatesHash()
        {
            var content = Encoding.UTF8.GetBytes("test update payload");
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var component = new ComponentUpdateInfo
            {
                Version = "1.0.1",
                DownloadUrl = "https://example.com/update.zip",
                Sha256 = hash
            };

            var handler = new FakeHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
                return response;
            });

            var httpClient = new HttpClient(handler);
            var logger = new TestLogger<ServiceUpdateDownloader>();
            var downloader = new ServiceUpdateDownloader(httpClient, logger);

            var result = await downloader.DownloadAsync(component, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.FilePath.Should().NotBeNull();
            File.Exists(result.FilePath!).Should().BeTrue();

            File.Delete(result.FilePath!);
        }

        [Fact]
        public async Task ServiceUpdateDownloader_ReturnsFailureOnHashMismatch()
        {
            var content = Encoding.UTF8.GetBytes("test update payload");
            var component = new ComponentUpdateInfo
            {
                Version = "1.0.1",
                DownloadUrl = "https://example.com/update.zip",
                Sha256 = "deadbeef"
            };

            var handler = new FakeHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
                return response;
            });

            var httpClient = new HttpClient(handler);
            var logger = new TestLogger<ServiceUpdateDownloader>();
            var downloader = new ServiceUpdateDownloader(httpClient, logger);

            var result = await downloader.DownloadAsync(component, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        [Fact]
        public async Task ServiceUpdateDownloader_ReturnsFailureOnHttpError()
        {
            var component = new ComponentUpdateInfo
            {
                Version = "1.0.1",
                DownloadUrl = "https://example.com/update.zip",
                Sha256 = "abc"
            };

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var httpClient = new HttpClient(handler);
            var logger = new TestLogger<ServiceUpdateDownloader>();
            var downloader = new ServiceUpdateDownloader(httpClient, logger);

            var result = await downloader.DownloadAsync(component, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        [Fact]
        public async Task ServiceUpdateInstaller_StagesAndHandsOffToUpdater_TriggersScmStopAndExit()
        {
            var tempSource = TestHelpers.CreateTempDirectory();
            var tempTarget = TestHelpers.CreateTempDirectory();
            var zipPath = Path.Combine(TestHelpers.CreateTempDirectory(), "update.zip");
            var updaterExePath = CreateSharedUpdaterExecutable(tempTarget);

            var sourceFile = Path.Combine(tempSource, "app", "test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            await File.WriteAllTextAsync(sourceFile, "updated");
            ZipFile.CreateFromDirectory(tempSource, zipPath);

            var installed = false;
            var scmStopRequested = false;
            var exitRequested = false;

            var installer = new AgentUpdateHandoffInstaller(
                new TestLogger<AgentUpdateHandoffInstaller>(),
                tempTarget,
                (_, _, _, _) =>
                {
                    installed = true;
                    return true;
                },
                _ =>
                {
                    scmStopRequested = true;
                    return true;
                },
                () => exitRequested = true);

            var result = await installer.InstallAsync(zipPath, CancellationToken.None);

            result.Success.Should().BeTrue();
            installed.Should().BeTrue();
            scmStopRequested.Should().BeTrue();
            exitRequested.Should().BeTrue();
            File.Exists(Path.Combine(tempTarget, "app", "test.txt")).Should().BeFalse();
        }

        [Fact]
        public async Task PluginUpdateChecker_ReturnsUpdatesForNewerPluginVersions()
        {
            var manifestJson = "{\"version\":\"1.0.0\",\"plugins\":[{\"name\":\"TestPlugin\",\"version\":\"2.0.0\",\"downloadUrl\":\"https://example.com/plugin.zip\",\"sha256\":\"abc\"}]}";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestJson)
            });

            var httpClient = new HttpClient(handler);
            var optionsMonitor = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions
            {
                Enabled = true,
                ManifestUrl = "https://example.com/manifest.json",
                CheckIntervalMinutes = 60
            });
            var logger = new TestLogger<PluginUpdateChecker>();
            var checker = new PluginUpdateChecker(httpClient, logger, optionsMonitor);

            var result = await checker.CheckForUpdatesAsync(CancellationToken.None);

            result.Updates.Should().ContainSingle();
            result.Updates[0].Id.Should().Be("TestPlugin");
        }

        [Fact]
        public async Task PluginUpdateDownloader_ReturnsFailureOnHashMismatch()
        {
            var content = Encoding.UTF8.GetBytes("plugin update payload");
            var plugin = new PluginUpdateInfo
            {
                Id = "TestPlugin",
                Version = "1.0.1",
                DownloadUrl = "https://example.com/plugin.zip",
                Sha256 = "deadbeef"
            };

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });

            var httpClient = new HttpClient(handler);
            var logger = new TestLogger<PluginUpdateDownloader>();
            var downloader = new PluginUpdateDownloader(httpClient, logger);

            var result = await downloader.DownloadAsync(plugin, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        [Fact]
        public async Task PluginUpdateInstaller_ExtractsAndCopiesFiles()
        {
            var tempSource = TestHelpers.CreateTempDirectory();
            var tempTarget = TestHelpers.CreateTempDirectory();
            var zipPath = Path.Combine(TestHelpers.CreateTempDirectory(), "plugin.zip");

            var sourceFile = Path.Combine(tempSource, "plugin", "plugin.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            await File.WriteAllTextAsync(sourceFile, "plugin");

            ZipFile.CreateFromDirectory(tempSource, zipPath);

            var logger = new TestLogger<PluginUpdateInstaller>();
            var installer = new PluginUpdateInstaller(logger, tempTarget);

            var result = await installer.InstallAsync(zipPath, CancellationToken.None);

            var targetFile = Path.Combine(tempTarget, "plugin", "plugin.dll");
            result.Success.Should().BeTrue();
            File.Exists(targetFile).Should().BeTrue();
        }

        [Fact]
        public async Task UnifiedUpdateChecker_RefreshSnapshotAsync_MapsAllCoreComponentsFromManifest()
        {
            var manifestJson = "{\"version\":\"3.0.0\",\"agent\":{\"version\":\"3.0.1\",\"downloadUrl\":\"https://updates.test/agent.zip\",\"sha256\":\"a\"},\"server\":{\"version\":\"3.0.2\",\"downloadUrl\":\"https://updates.test/server.zip\",\"sha256\":\"b\"},\"ui\":{\"version\":\"3.0.3\",\"downloadUrl\":\"https://updates.test/ui.zip\",\"sha256\":\"c\"},\"updater\":{\"version\":\"3.0.4\",\"downloadUrl\":\"https://updates.test/updater.zip\",\"sha256\":\"d\"}}";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestJson)
            });

            var httpClient = new HttpClient(handler);
            var options = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions
            {
                Enabled = true,
                ManifestUrl = "https://updates.test/manifest.json",
                CheckIntervalMinutes = 60
            });

            var checker = new UnifiedUpdateChecker(
                httpClient,
                options,
                new InMemoryUnifiedUpdateSnapshotStore(),
                new TestLogger<UnifiedUpdateChecker>());

            var snapshot = await checker.RefreshSnapshotAsync(CancellationToken.None);

            snapshot.Components.Should().HaveCount(4);
            snapshot.Components.Should().Contain(c => c.Component == "agent" && c.DownloadUrl == "https://updates.test/agent.zip");
            snapshot.Components.Should().Contain(c => c.Component == "server" && c.DownloadUrl == "https://updates.test/server.zip");
            snapshot.Components.Should().Contain(c => c.Component == "ui" && c.DownloadUrl == "https://updates.test/ui.zip");
            snapshot.Components.Should().Contain(c => c.Component == "updater" && c.DownloadUrl == "https://updates.test/updater.zip");
        }

        [Fact]
        public async Task UnifiedInstallOrchestrator_StartInstallAsync_WhenNoUpdatesAvailable_ReturnsSuccessfulNoOp()
        {
            var checker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo
            {
                AnyUpdateAvailable = false,
                Components =
                {
                    new UnifiedUpdateComponentStatus { Component = "agent", UpdateAvailable = false },
                    new UnifiedUpdateComponentStatus { Component = "server", UpdateAvailable = false },
                    new UnifiedUpdateComponentStatus { Component = "ui", UpdateAvailable = false },
                    new UnifiedUpdateComponentStatus { Component = "updater", UpdateAvailable = false }
                }
            });

            var downloader = new ServiceUpdateDownloader(
                new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                new TestLogger<ServiceUpdateDownloader>());

            var orchestrator = new UnifiedInstallOrchestrator(
                checker,
                downloader,
                new StubInstallPathResolver(TestHelpers.CreateTempDirectory()),
                new InMemoryCheckpointStore(),
                new TestLogger<UnifiedInstallOrchestrator>());

            var result = await orchestrator.StartInstallAsync(new UnifiedInstallUpdateRequest { UpdateAll = true }, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.UpdatedComponents.Should().BeEmpty();
            result.FailedComponents.Should().BeEmpty();
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("server")]
        public async Task UnifiedInstallOrchestrator_StopVerificationFails_SkipsUpdaterLaunch(string component)
        {
            var checker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo
            {
                AnyUpdateAvailable = true,
                Components =
                {
                    new UnifiedUpdateComponentStatus
                    {
                        Component = component,
                        UpdateAvailable = true,
                        LatestVersion = "9.0.0",
                        DownloadUrl = "https://updates.test/component.zip",
                        Sha256 = "abc"
                    }
                }
            });

            var updatePackage = Path.Combine(TestHelpers.CreateTempDirectory(), "component.zip");
            using (var archive = ZipFile.Open(updatePackage, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("component.bin");
                await using var stream = entry.Open();
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
            }

            var downloader = new FakeServiceUpdateDownloader(new UpdateDownloadResult
            {
                Success = true,
                FilePath = updatePackage
            });

            var updaterInvoked = false;
            var orchestrator = new UnifiedInstallOrchestrator(
                checker,
                downloader,
                new StubInstallPathResolver(TestHelpers.CreateTempDirectory()),
                new InMemoryCheckpointStore(),
                new TestLogger<UnifiedInstallOrchestrator>(),
                stopComponentBeforeUpdate: (_, _) => Task.FromResult((false, "forced stop failure")),
                runUpdaterProcess: (_, _, _) =>
                {
                    updaterInvoked = true;
                    return (true, (string?)null);
                });

            var result = await orchestrator.StartInstallAsync(new UnifiedInstallUpdateRequest
            {
                Components = new List<string> { component }
            }, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.FailedComponents.Should().Contain(component);
            result.ErrorMessage.Should().Contain("forced stop failure");
            updaterInvoked.Should().BeFalse();
        }

        [Fact]
        public async Task UnifiedInstallOrchestrator_StopSucceeds_InvokesUpdater()
        {
            var checker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo
            {
                AnyUpdateAvailable = true,
                Components =
                {
                    new UnifiedUpdateComponentStatus
                    {
                        Component = "ui",
                        UpdateAvailable = true,
                        LatestVersion = "9.1.0",
                        DownloadUrl = "https://updates.test/ui.zip",
                        Sha256 = "abc"
                    }
                }
            });

            var updatePackage = Path.Combine(TestHelpers.CreateTempDirectory(), "ui.zip");
            using (var archive = ZipFile.Open(updatePackage, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("ui.bin");
                await using var stream = entry.Open();
                await stream.WriteAsync(new byte[] { 9, 1, 0 });
            }

            var downloader = new FakeServiceUpdateDownloader(new UpdateDownloadResult
            {
                Success = true,
                FilePath = updatePackage
            });

            var updaterInvoked = false;
            var orchestrator = new UnifiedInstallOrchestrator(
                checker,
                downloader,
                new StubInstallPathResolver(TestHelpers.CreateTempDirectory()),
                new InMemoryCheckpointStore(),
                new TestLogger<UnifiedInstallOrchestrator>(),
                stopComponentBeforeUpdate: (_, _) => Task.FromResult((true, (string?)null)),
                runUpdaterProcess: (_, _, _) =>
                {
                    updaterInvoked = true;
                    return (true, (string?)null);
                });

            var result = await orchestrator.StartInstallAsync(new UnifiedInstallUpdateRequest
            {
                Components = new List<string> { "ui" }
            }, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.UpdatedComponents.Should().Contain("ui");
            updaterInvoked.Should().BeTrue();
        }

        private sealed class InMemoryCheckpointStore : IUnifiedInstallCheckpointStore
        {
            private UnifiedInstallCheckpoint? _checkpoint;

            public Task SaveCheckpointAsync(UnifiedInstallCheckpoint checkpoint, CancellationToken cancellationToken = default)
            {
                _checkpoint = checkpoint;
                return Task.CompletedTask;
            }

            public Task<UnifiedInstallCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_checkpoint);
            }

            public Task<UnifiedInstallCheckpointLoadResult> LoadCheckpointResultAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new UnifiedInstallCheckpointLoadResult
                {
                    Exists = _checkpoint != null,
                    IsCorrupted = false,
                    Checkpoint = _checkpoint
                });
            }

            public Task ClearCheckpointAsync(CancellationToken cancellationToken = default)
            {
                _checkpoint = null;
                return Task.CompletedTask;
            }

            public Task<bool> CheckpointExistsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_checkpoint != null);
            }
        }

        [Fact]
        public async Task AutoUpdateWorker_UsesTimerTicksToRunUpdateCycle()
        {
            var optionsMonitor = new TestOptionsMonitor<StorageWatchOptions>(new StorageWatchOptions { Mode = StorageWatchMode.Agent });
            var autoUpdateMonitor = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions
            {
                Enabled = true,
                CheckIntervalMinutes = 1
            });

            var serviceChecker = new FakeServiceUpdateChecker(new ComponentUpdateCheckResult { IsUpdateAvailable = false });
            var serviceDownloader = new FakeServiceUpdateDownloader(new UpdateDownloadResult { Success = false });
            var serviceInstaller = new FakeServiceUpdateInstaller(new UpdateInstallResult { Success = true });
            var pluginChecker = new FakePluginUpdateChecker(new PluginUpdateCheckResult { Updates = Array.Empty<PluginUpdateInfo>() });
            var pluginDownloader = new FakePluginUpdateDownloader(new PluginDownloadResult { Success = false });
            var pluginInstaller = new FakePluginUpdateInstaller(new UpdateInstallResult { Success = true });
            var unifiedChecker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo());
            var timerFactory = new FakeAutoUpdateTimerFactory(new[] { true, false });
            var logger = new RollingFileLogger(TestHelpers.CreateTempLogFile());

            var worker = new TestAutoUpdateWorker(optionsMonitor, autoUpdateMonitor, serviceChecker, serviceDownloader, serviceInstaller, pluginChecker, pluginDownloader, pluginInstaller, unifiedChecker, timerFactory, logger);

            await worker.RunAsync(CancellationToken.None);

            unifiedChecker.CallCount.Should().Be(2);
        }

        [Fact]
        public async Task AutoUpdateWorker_RunsInAgentMode()
        {
            var optionsMonitor = new TestOptionsMonitor<StorageWatchOptions>(new StorageWatchOptions { Mode = StorageWatchMode.Agent });
            var autoUpdateMonitor = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions { Enabled = true });

            var serviceChecker = new FakeServiceUpdateChecker(new ComponentUpdateCheckResult { IsUpdateAvailable = false });
            var serviceDownloader = new FakeServiceUpdateDownloader(new UpdateDownloadResult { Success = false });
            var serviceInstaller = new FakeServiceUpdateInstaller(new UpdateInstallResult { Success = true });
            var pluginChecker = new FakePluginUpdateChecker(new PluginUpdateCheckResult { Updates = Array.Empty<PluginUpdateInfo>() });
            var pluginDownloader = new FakePluginUpdateDownloader(new PluginDownloadResult { Success = false });
            var pluginInstaller = new FakePluginUpdateInstaller(new UpdateInstallResult { Success = true });
            var unifiedChecker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo());
            var timerFactory = new FakeAutoUpdateTimerFactory(new[] { true, false });
            var logger = new RollingFileLogger(TestHelpers.CreateTempLogFile());

            var worker = new TestAutoUpdateWorker(optionsMonitor, autoUpdateMonitor, serviceChecker, serviceDownloader, serviceInstaller, pluginChecker, pluginDownloader, pluginInstaller, unifiedChecker, timerFactory, logger);

            await worker.RunAsync(CancellationToken.None);

            unifiedChecker.CallCount.Should().Be(2);
        }

        [Fact]
        public async Task AutoUpdateWorker_DoesNotRunWhenDisabled()
        {
            var optionsMonitor = new TestOptionsMonitor<StorageWatchOptions>(new StorageWatchOptions { Mode = StorageWatchMode.Agent });
            var autoUpdateMonitor = new TestOptionsMonitor<AutoUpdateOptions>(new AutoUpdateOptions { Enabled = false });

            var serviceChecker = new FakeServiceUpdateChecker(new ComponentUpdateCheckResult { IsUpdateAvailable = false });
            var serviceDownloader = new FakeServiceUpdateDownloader(new UpdateDownloadResult { Success = false });
            var serviceInstaller = new FakeServiceUpdateInstaller(new UpdateInstallResult { Success = true });
            var pluginChecker = new FakePluginUpdateChecker(new PluginUpdateCheckResult { Updates = Array.Empty<PluginUpdateInfo>() });
            var pluginDownloader = new FakePluginUpdateDownloader(new PluginDownloadResult { Success = false });
            var pluginInstaller = new FakePluginUpdateInstaller(new UpdateInstallResult { Success = true });
            var unifiedChecker = new StubUnifiedUpdateChecker(new UnifiedUpdateStatusInfo());
            var timerFactory = new FakeAutoUpdateTimerFactory(new[] { true, false });
            var logger = new RollingFileLogger(TestHelpers.CreateTempLogFile());

            var worker = new TestAutoUpdateWorker(optionsMonitor, autoUpdateMonitor, serviceChecker, serviceDownloader, serviceInstaller, pluginChecker, pluginDownloader, pluginInstaller, unifiedChecker, timerFactory, logger);

            await worker.RunAsync(CancellationToken.None);

            serviceChecker.CallCount.Should().Be(0);
            serviceDownloader.CallCount.Should().Be(0);
            serviceInstaller.CallCount.Should().Be(0);
            pluginDownloader.CallCount.Should().Be(0);
            pluginInstaller.CallCount.Should().Be(0);
        }

        [Fact]
        public async Task ServiceUpdateInstaller_ReturnsFailure_WhenStagingDirectoryMissing()
        {
            var tempTarget = TestHelpers.CreateTempDirectory();
            var nonExistentStagingDir = Path.Combine(tempTarget, "non-existent-staging");
            var manifestPath = Path.Combine(nonExistentStagingDir, "manifest.json");
            _ = CreateSharedUpdaterExecutable(tempTarget);

            var updateLaunched = false;
            var installer = new AgentUpdateHandoffInstaller(
                new TestLogger<AgentUpdateHandoffInstaller>(),
                tempTarget,
                (_, _, _, _) =>
                {
                    updateLaunched = true;
                    return true;
                });

            var result = await installer.InstallAsync(manifestPath, CancellationToken.None);

            result.Success.Should().BeFalse();
            updateLaunched.Should().BeFalse();
        }

        private static string CreateSharedUpdaterExecutable(string installDir)
        {
            var updaterExePath = Path.GetFullPath(Path.Combine(installDir, "..", "Updater", "StorageWatch.Updater.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(updaterExePath)!);
            File.WriteAllText(updaterExePath, string.Empty);
            return updaterExePath;
        }

        [Fact]
        public async Task ServiceUpdateInstaller_ReturnsFailure_WhenManifestFileDoesNotExist()
        {
            var tempTarget = TestHelpers.CreateTempDirectory();
            var stagingDir = Path.Combine(tempTarget, "staging");
            Directory.CreateDirectory(stagingDir);
            var manifestPath = Path.Combine(stagingDir, "manifest.json");

            var updateLaunched = false;
            var installer = new AgentUpdateHandoffInstaller(
                new TestLogger<AgentUpdateHandoffInstaller>(),
                tempTarget,
                (_, _, _, _) =>
                {
                    updateLaunched = true;
                    return true;
                });

            var result = await installer.InstallAsync(manifestPath, CancellationToken.None);

            result.Success.Should().BeFalse();
            updateLaunched.Should().BeFalse();
        }

        [Fact]
        public async Task ServiceUpdateDownloader_ReturnsFailure_OnNetworkTimeout()
        {
            var component = new ComponentUpdateInfo
            {
                Version = "1.0.1",
                DownloadUrl = "https://example.com/update.zip",
                Sha256 = "abc123"
            };

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("timeout"));
            var httpClient = new HttpClient(handler);
            var downloader = new ServiceUpdateDownloader(httpClient, new TestLogger<ServiceUpdateDownloader>());

            var result = await downloader.DownloadAsync(component, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        [Fact]
        public void UnifiedInstallCheckpointValidator_ReturnsInProgress_WhenCheckpointIsFreshAndVersionAdvances()
        {
            var root = TestHelpers.CreateTempDirectory();
            var installedVersion = CreateVersionedInstallLayout(root);
            var targetVersion = new Version(installedVersion.Major + 1, 0, 0, 0).ToString();
            var checkpoint = CreateCheckpoint("agent", ComponentInstallState.InProgress, targetVersion, DateTimeOffset.UtcNow, includeZip: true);

            var validator = new UnifiedInstallCheckpointValidator(
                new StubInstallPathResolver(root),
                new TestLogger<UnifiedInstallCheckpointValidator>());

            var result = validator.Validate(checkpoint);

            result.State.Should().Be(UnifiedInstallResumeState.InProgress);
            result.ShouldResume.Should().BeTrue();
            result.ShouldDelete.Should().BeFalse();
        }

        [Fact]
        public void UnifiedInstallCheckpointValidator_ReturnsStale_WhenCheckpointIsTooOld()
        {
            var root = TestHelpers.CreateTempDirectory();
            var installedVersion = CreateVersionedInstallLayout(root);
            var targetVersion = new Version(installedVersion.Major + 1, 0, 0, 0).ToString();
            var checkpoint = CreateCheckpoint("agent", ComponentInstallState.InProgress, targetVersion, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20), includeZip: true);

            var validator = new UnifiedInstallCheckpointValidator(
                new StubInstallPathResolver(root),
                new TestLogger<UnifiedInstallCheckpointValidator>());

            var result = validator.Validate(checkpoint);

            result.State.Should().Be(UnifiedInstallResumeState.Stale);
            result.ShouldDelete.Should().BeTrue();
        }

        [Fact]
        public async Task UnifiedInstallResumeService_DeletesStaleCheckpointAndDoesNotResume()
        {
            var root = TestHelpers.CreateTempDirectory();
            var installedVersion = CreateVersionedInstallLayout(root);
            var targetVersion = new Version(installedVersion.Major + 1, 0, 0, 0).ToString();
            var checkpoint = CreateCheckpoint("agent", ComponentInstallState.InProgress, targetVersion, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20), includeZip: true);

            var store = new InMemoryCheckpointStore();
            await store.SaveCheckpointAsync(checkpoint, CancellationToken.None);

            var validator = new UnifiedInstallCheckpointValidator(
                new StubInstallPathResolver(root),
                new TestLogger<UnifiedInstallCheckpointValidator>());
            var orchestrator = new RecordingUnifiedInstallOrchestrator();
            var service = new UnifiedInstallResumeService(
                store,
                validator,
                orchestrator,
                new TestLogger<UnifiedInstallResumeService>());

            await service.StartAsync(CancellationToken.None);

            orchestrator.ResumeCallCount.Should().Be(0);
            (await store.CheckpointExistsAsync()).Should().BeFalse();
        }

        [Fact]
        public async Task UnifiedInstallResumeService_ResumesOnlyWhenValidationPasses()
        {
            var root = TestHelpers.CreateTempDirectory();
            var installedVersion = CreateVersionedInstallLayout(root);
            var targetVersion = new Version(installedVersion.Major + 1, 0, 0, 0).ToString();
            var checkpoint = CreateCheckpoint("agent", ComponentInstallState.InProgress, targetVersion, DateTimeOffset.UtcNow, includeZip: true);

            var store = new InMemoryCheckpointStore();
            await store.SaveCheckpointAsync(checkpoint, CancellationToken.None);

            var validator = new UnifiedInstallCheckpointValidator(
                new StubInstallPathResolver(root),
                new TestLogger<UnifiedInstallCheckpointValidator>());
            var orchestrator = new RecordingUnifiedInstallOrchestrator();
            var service = new UnifiedInstallResumeService(
                store,
                validator,
                orchestrator,
                new TestLogger<UnifiedInstallResumeService>());

            await service.StartAsync(CancellationToken.None);
            await orchestrator.ResumeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            orchestrator.ResumeCallCount.Should().Be(1);
            (await store.CheckpointExistsAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task NetworkTimeoutDownloader_Failure_Regresses_NoResumeCollision()
        {
            var component = new ComponentUpdateInfo
            {
                Version = "1.0.1",
                DownloadUrl = "https://example.com/update.zip",
                Sha256 = "abc123"
            };

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("timeout"));
            var httpClient = new HttpClient(handler);
            var downloader = new ServiceUpdateDownloader(httpClient, new TestLogger<ServiceUpdateDownloader>());

            var result = await downloader.DownloadAsync(component, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        private static UnifiedInstallCheckpoint CreateCheckpoint(string component, ComponentInstallState state, string targetVersion, DateTimeOffset lastUpdatedAtUtc, bool includeZip)
        {
            var checkpoint = new UnifiedInstallCheckpoint
            {
                OrchestrationId = Guid.NewGuid().ToString("N"),
                StartedAtUtc = lastUpdatedAtUtc,
                LastUpdatedAtUtc = lastUpdatedAtUtc,
                IsInstalling = true,
                Components = new List<string> { component },
                CurrentComponentIndex = 0,
                ComponentStates = new List<ComponentCheckpointState>
                {
                    new()
                    {
                        Component = component,
                        State = state,
                        TargetVersion = targetVersion,
                        DownloadUrl = "https://example.com/update.zip",
                        Sha256 = "abc123",
                        LocalZipPath = null,
                        UpdatedAtUtc = lastUpdatedAtUtc
                    }
                }
            };

            if (includeZip)
            {
                var zipPath = Path.Combine(TestHelpers.CreateTempDirectory(), "update.zip");
                File.WriteAllText(zipPath, "placeholder");
                checkpoint.ComponentStates[0].LocalZipPath = zipPath;
            }

            return checkpoint;
        }

        private static Version CreateVersionedInstallLayout(string root)
        {
            var sourceAssemblyPath = typeof(AutoUpdateTests).Assembly.Location;
            var fileVersion = FileVersionInfo.GetVersionInfo(sourceAssemblyPath).FileVersion ?? "1.0.0.0";
            var installedVersion = Version.TryParse(fileVersion, out var version) ? version : new Version(1, 0, 0, 0);

            CopyAssemblyAsExecutable(sourceAssemblyPath, Path.Combine(root, "Agent", "StorageWatchAgent.exe"));
            CopyAssemblyAsExecutable(sourceAssemblyPath, Path.Combine(root, "Server", "StorageWatchServer.exe"));
            CopyAssemblyAsExecutable(sourceAssemblyPath, Path.Combine(root, "UI", "StorageWatchUI.exe"));
            CopyAssemblyAsExecutable(sourceAssemblyPath, Path.Combine(root, "Updater", "StorageWatch.Updater.exe"));

            return installedVersion;
        }

        private static void CopyAssemblyAsExecutable(string sourceAssemblyPath, string targetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourceAssemblyPath, targetPath, overwrite: true);
        }

        private sealed class RecordingUnifiedInstallOrchestrator : IUnifiedInstallOrchestrator
        {
            public int ResumeCallCount { get; private set; }

            public TaskCompletionSource<bool> ResumeCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<UnifiedUpdateInstallResult> StartInstallAsync(UnifiedInstallUpdateRequest request, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public Task<UnifiedUpdateInstallResult> ResumePendingInstallAsync(CancellationToken cancellationToken)
            {
                ResumeCallCount++;
                ResumeCalled.TrySetResult(true);
                return Task.FromResult(new UnifiedUpdateInstallResult
                {
                    OrchestrationId = Guid.NewGuid().ToString("N"),
                    Success = true,
                    RestartRequired = false,
                    ErrorMessage = null,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public UnifiedUpdateProgressInfo GetProgress()
            {
                return new UnifiedUpdateProgressInfo
                {
                    OrchestrationId = string.Empty,
                    Phase = "idle",
                    Component = string.Empty,
                    Status = "Idle",
                    ProgressPercent = 0,
                    IsIndeterminate = false
                };
            }

            public UnifiedUpdateInstallResult GetLastResult()
            {
                return new UnifiedUpdateInstallResult
                {
                    OrchestrationId = string.Empty,
                    Success = true,
                    RestartRequired = false,
                    ErrorMessage = null,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private sealed class StubUnifiedUpdateChecker : IUnifiedUpdateChecker
        {
            private readonly UnifiedUpdateStatusInfo _status;

            public StubUnifiedUpdateChecker(UnifiedUpdateStatusInfo status)
            {
                _status = status;
            }

            public Task<UnifiedUpdateStatusInfo> RefreshSnapshotAsync(CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_status);
            }

            public int CallCount { get; private set; }

            public UnifiedUpdateStatusInfo GetLatestSnapshot()
            {
                return _status;
            }

            public Task SetInstallingStateAsync(bool isInstalling, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class StubInstallPathResolver : global::StorageWatch.Services.AutoUpdate.IInstallPathResolver
        {
            private readonly string _root;

            public StubInstallPathResolver(string root)
            {
                _root = root;
                Directory.CreateDirectory(Path.Combine(_root, "Updater"));
                var updaterPath = Path.Combine(_root, "Updater", "StorageWatch.Updater.exe");
                if (!File.Exists(updaterPath))
                {
                    File.WriteAllText(updaterPath, string.Empty);
                }
            }

            public global::StorageWatch.Services.AutoUpdate.ResolvedInstallPaths Resolve()
            {
                return new global::StorageWatch.Services.AutoUpdate.ResolvedInstallPaths
                {
                    InstallRoot = _root,
                    AgentDirectory = Path.Combine(_root, "Agent"),
                    ServerDirectory = Path.Combine(_root, "Server"),
                    UiDirectory = Path.Combine(_root, "UI"),
                    UpdaterDirectory = Path.Combine(_root, "Updater"),
                    UpdaterExecutablePath = Path.Combine(_root, "Updater", "StorageWatch.Updater.exe"),
                    UsedRegistryInstallRoot = false
                };
            }
        }

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        private sealed class FakeRestartHandler : IServiceRestartHandler
        {
            public bool RestartRequested { get; private set; }

            public void RequestRestart()
            {
                RestartRequested = true;
            }
        }

        private sealed class ThrowingRestartHandler : IServiceRestartHandler
        {
            public void RequestRestart()
            {
                throw new InvalidOperationException("Restart failed");
            }
        }

        private sealed class AssertiveRestartHandler : IServiceRestartHandler
        {
            private readonly Action? _onRequest;

            public AssertiveRestartHandler(Action? onRequest = null)
            {
                _onRequest = onRequest;
            }

            public int RequestCount { get; private set; }

            public void RequestRestart()
            {
                RequestCount++;
                _onRequest?.Invoke();
            }
        }

        private sealed class FakeServiceUpdateChecker : IServiceUpdateChecker
        {
            private readonly ComponentUpdateCheckResult _result;

            public FakeServiceUpdateChecker(ComponentUpdateCheckResult result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public Task<ComponentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakePluginUpdateChecker : IPluginUpdateChecker
        {
            private readonly PluginUpdateCheckResult _result;

            public FakePluginUpdateChecker(PluginUpdateCheckResult result)
            {
                _result = result;
            }

            public Task<PluginUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeServiceUpdateDownloader : IServiceUpdateDownloader
        {
            private readonly UpdateDownloadResult _result;

            public FakeServiceUpdateDownloader(UpdateDownloadResult result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public Task<UpdateDownloadResult> DownloadAsync(ComponentUpdateInfo component, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakePluginUpdateDownloader : IPluginUpdateDownloader
        {
            private readonly PluginDownloadResult _result;

            public FakePluginUpdateDownloader(PluginDownloadResult result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public Task<PluginDownloadResult> DownloadAsync(PluginUpdateInfo plugin, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeServiceUpdateInstaller : IServiceUpdateInstaller
        {
            private readonly UpdateInstallResult _result;

            public FakeServiceUpdateInstaller(UpdateInstallResult result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public Task<UpdateInstallResult> InstallAsync(string zipPath, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakePluginUpdateInstaller : IPluginUpdateInstaller
        {
            private readonly UpdateInstallResult _result;

            public FakePluginUpdateInstaller(UpdateInstallResult result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public Task<UpdateInstallResult> InstallAsync(string zipPath, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeAutoUpdateTimerFactory : IAutoUpdateTimerFactory
        {
            private readonly bool[] _ticks;

            public FakeAutoUpdateTimerFactory(bool[] ticks)
            {
                _ticks = ticks;
            }

            public IAutoUpdateTimer Create(TimeSpan interval)
            {
                return new FakeAutoUpdateTimer(_ticks);
            }
        }

        private sealed class FakeAutoUpdateTimer : IAutoUpdateTimer
        {
            private readonly bool[] _ticks;
            private int _index;

            public FakeAutoUpdateTimer(bool[] ticks)
            {
                _ticks = ticks;
            }

            public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
            {
                var next = _index < _ticks.Length && _ticks[_index];
                _index++;
                return ValueTask.FromResult(next);
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        private sealed class TestAutoUpdateWorker : AutoUpdateWorker
        {
            public TestAutoUpdateWorker(
                Microsoft.Extensions.Options.IOptionsMonitor<StorageWatchOptions> storageOptionsMonitor,
                Microsoft.Extensions.Options.IOptionsMonitor<AutoUpdateOptions> autoUpdateOptionsMonitor,
                IServiceUpdateChecker serviceUpdateChecker,
                IServiceUpdateDownloader serviceUpdateDownloader,
                IServiceUpdateInstaller serviceUpdateInstaller,
                IPluginUpdateChecker pluginUpdateChecker,
                IPluginUpdateDownloader pluginUpdateDownloader,
                IPluginUpdateInstaller pluginUpdateInstaller,
                IUnifiedUpdateChecker unifiedUpdateChecker,
                IAutoUpdateTimerFactory timerFactory,
                RollingFileLogger logger)
                : base(storageOptionsMonitor, autoUpdateOptionsMonitor, serviceUpdateChecker, serviceUpdateDownloader, serviceUpdateInstaller, pluginUpdateChecker, pluginUpdateDownloader, pluginUpdateInstaller, unifiedUpdateChecker, timerFactory, logger)
            {
            }

            public Task RunAsync(CancellationToken token)
            {
                return ExecuteAsync(token);
            }
        }

        private static HashSet<string> SnapshotTempChildDirectories(string folderName)
        {
            var root = Path.Combine(Path.GetTempPath(), folderName);
            return new HashSet<string>(
                Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
