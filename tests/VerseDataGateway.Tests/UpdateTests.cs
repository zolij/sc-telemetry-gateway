using VerseDataGateway.Windows;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace VerseDataGateway.Tests;

public sealed class UpdateTests
{
    [Fact]
    public async Task CheckDoesNotDownloadOrInstallWithoutApproval()
    {
        var manager = new FakeManager();
        var updater = new ApplicationUpdater(manager);
        Assert.Equal("2.0.0", await updater.CheckAsync());
        Assert.False(manager.Downloaded);
        Assert.Throws<InvalidOperationException>(updater.ScheduleInstall);
        await updater.DownloadAsync(_ => { }, CancellationToken.None);
        Assert.True(manager.Downloaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledDownloadCannotBeInstalled(bool cancelled)
    {
        var manager = new FakeManager { FailDownload = !cancelled };
        var updater = new ApplicationUpdater(manager);
        await updater.CheckAsync();
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.DownloadAsync(_ => { }, new CancellationToken(true)));
        else
            await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(_ => { }, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(updater.ScheduleInstall);
    }

    [Fact]
    public async Task NewCheckInvalidatesPreviouslyDownloadedOffer()
    {
        var manager = new FakeManager();
        var updater = new ApplicationUpdater(manager);
        await updater.CheckAsync();
        await updater.DownloadAsync(_ => { }, CancellationToken.None);
        manager.HasUpdate = false;
        Assert.Null(await updater.CheckAsync());
        Assert.Throws<InvalidOperationException>(updater.ScheduleInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(_ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task PortableCopyCannotStartManagedUpdates()
    {
        var manager = new FakeManager { Portable = true };
        var updater = new ApplicationUpdater(manager);
        Assert.False(updater.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(updater.CheckAsync);
        Assert.Equal(0, manager.Checks);
    }

    private sealed class FakeManager() : UpdateManager(
        new SimpleWebSource("https://example.invalid"),
        new UpdateOptions { ExplicitChannel = "win" },
        new TestVelopackLocator("Test.App", "1.0.0", Path.GetTempPath()))
    {
        public override bool IsInstalled => true;
        public override bool IsPortable => Portable;
        public bool Portable { get; set; }
        public bool HasUpdate { get; set; } = true;
        public bool FailDownload { get; set; }
        public bool Downloaded { get; private set; }
        public int Checks { get; private set; }
        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            Checks++;
            return Task.FromResult(HasUpdate ? new UpdateInfo(new VelopackAsset
            {
                PackageId = "Test.App", Version = SemanticVersion.Parse("2.0.0"),
                FileName = "Test.App-2.0.0-full.nupkg", Type = VelopackAssetType.Full
            }, false) : null);
        }
        public override Task DownloadUpdatesAsync(UpdateInfo update, Action<int>? progress = null, CancellationToken cancelToken = default)
        {
            cancelToken.ThrowIfCancellationRequested();
            if (FailDownload) throw new IOException("Synthetic download failure.");
            Downloaded = true;
            return Task.CompletedTask;
        }
    }
}
