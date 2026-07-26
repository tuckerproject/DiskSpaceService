using StorageWatch.Updater;
using StorageWatch.Updater.Tests.Fixtures;
using StorageWatch.Updater.Tests.Helpers;
using System.Text;
using System.Text.Json;

namespace StorageWatch.Updater.Tests.Integration;

[Collection("UpdaterIntegrationSequential")]
public class RestartBehaviorIntegrationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    [Fact]
    [Trait("Category", "Integration")]
    public void UIRestartHelper_WhenLauncherSucceeds_ShouldReturnTrueAndCaptureLaunchAttempt()
    {
        var launcher = new FakeProcessLauncher();
        var helper = new UIRestartHelper(launcher);
        var uiPath = _temp.CreateFile("ui/StorageWatchUI.exe", string.Empty);

        var result = helper.TryRestartUI(uiPath);

        result.Should().BeTrue();
        launcher.StartedProcesses.Should().ContainSingle();
        launcher.StartedProcesses[0].FileName.Should().Be(uiPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ServerRestartHelper_WhenServiceNameMissing_ShouldReturnFalse()
    {
        var helper = new ServerRestartHelper();

        var result = helper.TryRestartServer(string.Empty);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CheckpointHandoffStore_PreservesRestartIntent_WhenAgentHandoffCompletes()
    {
        var checkpointPath = _temp.CreateFile("Update/install-plan.json", "{\"orchestrationId\":\"test\",\"restartUIRequested\":false,\"restartServerRequested\":false}");
        var logs = new List<string>();

        CheckpointHandoffStore.TryPersistRestartIntent(checkpointPath, restartUiRequested: true, restartServerRequested: true, logs.Add).Should().BeTrue();
        CheckpointHandoffStore.TryPersistAgentHandoffComplete(checkpointPath, logs.Add).Should().BeTrue();

        using var document = JsonDocument.Parse(File.ReadAllText(checkpointPath));
        var root = document.RootElement;
        root.GetProperty("restartUIRequested").GetBoolean().Should().BeTrue();
        root.GetProperty("restartServerRequested").GetBoolean().Should().BeTrue();
        root.GetProperty("handoffCompletedAtUtc").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        root.GetProperty("handoffState").GetInt32().Should().Be(3);
        logs.Should().Contain(message => message.Contains("Persisted restart intent", StringComparison.Ordinal));
        logs.Should().Contain(message => message.Contains("Persisted handoff-complete marker", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SelfUpdateApply_WithContinuationArgs_ShouldLaunchUpdatedUpdaterWithContinuation()
    {
        var updaterExe = _temp.CreateFile("current/StorageWatch.Updater.exe", "current");
        var currentFolder = Path.GetDirectoryName(updaterExe)!;
        var staging = _temp.CreateDirectory("staging");
        var target = _temp.CreateDirectory("target");

        _temp.CreateFile("staging/StorageWatch.Updater.exe", "updated");

        var continuationArgs = new[] { "--update-ui", "--target", "C:/fake" };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(continuationArgs)));

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Not used")));
        var launcher = new FakeProcessLauncher();
        var manager = new SelfUpdateManager(updaterExe, currentFolder, httpClient, launcher, _ => { });

        var args = new UpdaterArguments
        {
            SelfUpdateApply = true,
            SelfUpdateStagingPath = staging,
            TargetPath = target,
            ContinueArguments = encoded
        };

        var result = await manager.RunSelfUpdateApplyAsync(args);

        result.Should().BeTrue();
        launcher.StartedProcesses.Should().ContainSingle();
        var startInfo = launcher.StartedProcesses[0];
        startInfo.FileName.Should().Be(Path.Combine(target, "StorageWatch.Updater.exe"));
        startInfo.ArgumentList.Should().ContainInOrder("--update-ui", "--target", "C:/fake");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }
}
