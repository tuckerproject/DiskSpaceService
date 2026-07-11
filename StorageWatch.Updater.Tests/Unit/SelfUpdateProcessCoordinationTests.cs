using StorageWatch.Shared.Update.Models;
using StorageWatch.Updater;
using StorageWatch.Updater.Tests.Fixtures;
using StorageWatch.Updater.Tests.Helpers;
using System.IO.Compression;
using System.Net;

namespace StorageWatch.Updater.Tests.Unit;

public class SelfUpdateProcessCoordinationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunLegacySelfUpdateStageAsync_ShouldPassWaitForPidToApplyProcess()
    {
        var updaterExe = _temp.CreateFile("updater/StorageWatch.Updater.exe", string.Empty);
        var updaterFolder = Path.GetDirectoryName(updaterExe)!;
        var packageBytes = CreateSelfUpdatePackageBytes();
        var packageHash = HashTestUtilities.ComputeSha256(packageBytes);

        using var httpClient = new HttpClient(FakeHttpMessageHandler.FromBytes("https://updates.test/updater.zip", packageBytes));
        var processLauncher = new FakeProcessLauncher();
        var manager = new SelfUpdateManager(updaterExe, updaterFolder, httpClient, processLauncher, _ => { });

        var component = new ComponentUpdateInfo
        {
            Version = "99.0.0.0",
            DownloadUrl = "https://updates.test/updater.zip",
            Sha256 = packageHash
        };

        var staged = await manager.RunLegacySelfUpdateStageAsync(component, new UpdaterArguments { UpdateUI = true });

        staged.Should().BeTrue();
        processLauncher.StartedProcesses.Should().ContainSingle();
        var startInfo = processLauncher.StartedProcesses.Single();
        startInfo.ArgumentList.Should().Contain("--self-update-apply");
        startInfo.ArgumentList.Should().Contain("--wait-for-pid");
        startInfo.ArgumentList.Should().Contain(Environment.ProcessId.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunSelfUpdateApplyAsync_WhenWaitForPidAlreadyExited_ShouldApplySuccessfully()
    {
        var updaterExe = _temp.CreateFile("updater/StorageWatch.Updater.exe", string.Empty);
        var updaterFolder = Path.GetDirectoryName(updaterExe)!;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var processLauncher = new FakeProcessLauncher();
        var manager = new SelfUpdateManager(updaterExe, updaterFolder, httpClient, processLauncher);

        var staging = _temp.CreateDirectory("staging");
        var target = _temp.CreateDirectory("target");
        File.WriteAllText(Path.Combine(staging, "StorageWatch.Updater.exe"), "new-binary");
        File.WriteAllText(Path.Combine(staging, "new-file.txt"), "new");
        File.WriteAllText(Path.Combine(target, "old-file.txt"), "old");

        var exitedProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Milliseconds 100" }
        })!;

        var args = new UpdaterArguments
        {
            SelfUpdateApply = true,
            SelfUpdateStagingPath = staging,
            TargetPath = target,
            WaitForPid = exitedProcess.Id
        };

        var applied = await manager.RunSelfUpdateApplyAsync(args);

        applied.Should().BeTrue();
        File.Exists(Path.Combine(target, "new-file.txt")).Should().BeTrue();
        File.Exists(Path.Combine(target, "old-file.txt")).Should().BeFalse();
    }

    private static byte[] CreateSelfUpdatePackageBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("StorageWatch.Updater.exe");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("fake-updater-binary");
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        _temp.Dispose();
    }
}
