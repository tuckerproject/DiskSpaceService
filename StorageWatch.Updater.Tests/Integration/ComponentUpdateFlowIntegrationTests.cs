using StorageWatch.Updater.Tests.Fixtures;
using StorageWatch.Updater.Tests.Helpers;

namespace StorageWatch.Updater.Tests.Integration;

[Collection("UpdaterIntegrationSequential")]
public class ComponentUpdateFlowIntegrationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateUi_WhenSourceAndTargetValid_ShouldCopyFilesAndExitSuccess()
    {
        var updaterExe = TestArtifactLocator.GetUpdaterExePath();
        if (!File.Exists(updaterExe))
        {
            return;
        }

        var source = _temp.CreateDirectory("ui-source");
        var target = _temp.CreateDirectory("ui-target");

        File.WriteAllText(Path.Combine(source, "ui-content.txt"), "ui-update");
        var safeUiExe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        File.Copy(safeUiExe, Path.Combine(target, "StorageWatchUI.exe"), overwrite: true);

        var (exitCode, stdOut, stdErr) = await ProcessTestUtilities.RunProcessAsync(
            updaterExe,
            $"--update-ui --source \"{source}\" --target \"{target}\"");

        exitCode.Should().Be(0, $"stdout: {stdOut}{Environment.NewLine}stderr: {stdErr}");
        File.ReadAllText(Path.Combine(target, "ui-content.txt")).Should().Be("ui-update");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateServer_WhenSourceAndTargetValid_ShouldCopyFilesAndExitSuccess()
    {
        var updaterExe = TestArtifactLocator.GetUpdaterExePath();
        if (!File.Exists(updaterExe))
        {
            return;
        }

        var source = _temp.CreateDirectory("server-source");
        var target = _temp.CreateDirectory("server-target");

        File.WriteAllText(Path.Combine(source, "server-content.txt"), "server-update");
        var safeServerExe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        File.Copy(safeServerExe, Path.Combine(target, "StorageWatchServer.exe"), overwrite: true);

        var (exitCode, stdOut, stdErr) = await ProcessTestUtilities.RunProcessAsync(
            updaterExe,
            $"--update-server --source \"{source}\" --target \"{target}\"");

        exitCode.Should().Be(0, $"stdout: {stdOut}{Environment.NewLine}stderr: {stdErr}");
        File.ReadAllText(Path.Combine(target, "server-content.txt")).Should().Be("server-update");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateAgent_WhenServiceStopFails_ShouldExitWithErrorBeforeCopy()
    {
        var updaterExe = TestArtifactLocator.GetUpdaterExePath();
        if (!File.Exists(updaterExe))
        {
            return;
        }

        var source = _temp.CreateDirectory("agent-source");
        var target = _temp.CreateDirectory("agent-target");

        File.WriteAllText(Path.Combine(source, "agent-content.txt"), "agent-update");

        var (exitCode, stdOut, stdErr) = await ProcessTestUtilities.RunProcessAsync(
            updaterExe,
            $"--update-agent --source \"{source}\" --target \"{target}\"",
            environmentVariables: new Dictionary<string, string?>
            {
                ["STORAGEWATCH_AGENT_SERVICE_NAME"] = "StorageWatchAgent_DoesNotExist_Test"
            });

        exitCode.Should().Be(2, $"stdout: {stdOut}{Environment.NewLine}stderr: {stdErr}");
        File.Exists(Path.Combine(target, "agent-content.txt")).Should().BeFalse();
    }

    public void Dispose()
    {
        _temp.Dispose();
    }
}
