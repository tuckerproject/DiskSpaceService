using StorageWatch.Updater;

namespace StorageWatch.Updater.Tests.Unit;

public class ContinuationArgumentsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void BuildContinuationArguments_WhenUpdateUiProvided_ShouldIncludeUpdateAndRestartFlags()
    {
        var args = new UpdaterArguments
        {
            UpdateUI = true,
            SourcePath = "C:/temp/source",
            TargetPath = "C:/temp/target",
            ManifestPath = "C:/temp/manifest.json",
            RestartUI = true
        };

        var continuation = SelfUpdateManager.BuildContinuationArguments(args);

        continuation.Should().ContainInOrder(
            "--update-ui",
            "--source", "C:/temp/source",
            "--target", "C:/temp/target",
            "--manifest", "C:/temp/manifest.json",
            "--restart-ui",
            "--allow-system-restart-intent");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildContinuationArguments_WhenRestartOnlyProvided_ShouldReturnSingleRestartArg()
    {
        var args = new UpdaterArguments { RestartServer = true };

        var continuation = SelfUpdateManager.BuildContinuationArguments(args);

        continuation.Should().Equal("--restart-server");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildContinuationArguments_WhenNoActionProvided_ShouldReturnEmpty()
    {
        var args = new UpdaterArguments();

        var continuation = SelfUpdateManager.BuildContinuationArguments(args);

        continuation.Should().BeEmpty();
    }
}
