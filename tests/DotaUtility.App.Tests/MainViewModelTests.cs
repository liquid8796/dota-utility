using DotaUtility.App.Services;
using DotaUtility.App.ViewModels;
using DotaUtility.Application;
using DotaUtility.Domain;

namespace DotaUtility.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task StartAsync_MapsVisibleSnapshotToDashboardText()
    {
        var snapshot = new VisibilitySnapshot(
            VisibilityState.Visible,
            new DateTimeOffset(2026, 9, 18, 3, 20, 0, TimeSpan.FromHours(7)),
            true,
            21328,
            2,
            3,
            1 << 3,
            "Enemy team currently has vision.",
            0x7FF61234ABCD);
        var logger = new RecordingLogger();
        await using var monitor = new VisibilityMonitor(new FixedProbe(snapshot), TimeSpan.FromMinutes(1));
        await using var viewModel = new MainViewModel(monitor, logger, synchronizationContext: null);

        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.State == VisibilityState.Visible);

        Assert.Equal("VISIBLE", viewModel.StatusHeadline);
        Assert.Equal("Radiant", viewModel.LocalTeamText);
        Assert.Equal("Dire", viewModel.EnemyTeamText);
        Assert.Equal("0x00000008", viewModel.VisibilityMaskText);
        Assert.Equal("PID 21328", viewModel.ProcessText);
        Assert.Equal("0x00007FF61234ABCD", viewModel.HeroAddressText);
        Assert.Equal("CONNECTED", viewModel.ConnectionText);
        Assert.Same(snapshot, logger.LastSnapshot);
    }

    [Fact]
    public async Task StartAsync_MapsDisconnectedUnknownToWaitingState()
    {
        var snapshot = VisibilitySnapshot.Unknown("dota2.exe is not running.");
        var logger = new RecordingLogger();
        await using var monitor = new VisibilityMonitor(new FixedProbe(snapshot), TimeSpan.FromMinutes(1));
        await using var viewModel = new MainViewModel(monitor, logger, synchronizationContext: null);

        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.StatusDetail.Contains("not running", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("WAITING", viewModel.StatusHeadline);
        Assert.Equal("DISCONNECTED", viewModel.ConnectionText);
        Assert.Equal("—", viewModel.ProcessText);
        Assert.Equal("Unknown", viewModel.LocalTeamText);
        Assert.Equal("—", viewModel.HeroAddressText);
    }

    [Fact]
    public void FileSessionLogger_SanitizesControlCharactersAndDeduplicatesAfterSuccessfulWrite()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dota-utility-log-{Guid.NewGuid():N}.log");
        try
        {
            var logger = new FileSessionLogger(tempFile);
            var snapshot = VisibilitySnapshot.Unknown("line1\r\njected\u001b[31mred");

            logger.LogSnapshot(snapshot);
            logger.LogSnapshot(snapshot);

            var lines = File.ReadAllLines(tempFile);
            Assert.Single(lines);
            Assert.DoesNotContain("\u001b", lines[0], StringComparison.Ordinal);
            Assert.Contains("line1  jected [31mred", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task CloseSettings_PersistsRememberChoiceAndCloseBehaviorAcrossInstances()
    {
        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"dota-utility-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new FileAppSettingsService(tempSettingsFile);
            var logger = new RecordingLogger();
            await using var monitor = new VisibilityMonitor(
                new FixedProbe(VisibilitySnapshot.Unknown("idle")),
                TimeSpan.FromMinutes(1));
            await using var viewModel = new MainViewModel(
                monitor,
                logger,
                synchronizationContext: null,
                settingsService: settingsService);

            Assert.True(viewModel.IsDashboardTabSelected);
            Assert.False(viewModel.IsSettingsTabSelected);
            Assert.Equal(CloseBehavior.MinimizeToTray, viewModel.CloseBehavior);
            Assert.True(viewModel.MinimizeToTrayOnClose);
            Assert.False(viewModel.ExitOnClose);
            Assert.False(viewModel.RememberCloseChoice);
            Assert.False(viewModel.ShowEnemyHeroesOnMinimap);

            viewModel.ShowEnemyHeroesOnMinimap = true;
            Assert.True(viewModel.ShowEnemyHeroesOnMinimap);

            viewModel.IsSettingsTabSelected = true;
            Assert.Equal(1, viewModel.SelectedTabIndex);
            Assert.False(viewModel.IsDashboardTabSelected);
            Assert.True(viewModel.IsSettingsTabSelected);

            viewModel.ApplyCloseDecision(CloseBehavior.ExitApplication, rememberChoice: true);
            Assert.Equal(CloseBehavior.ExitApplication, viewModel.CloseBehavior);
            Assert.False(viewModel.MinimizeToTrayOnClose);
            Assert.True(viewModel.ExitOnClose);
            Assert.True(viewModel.RememberCloseChoice);
            Assert.True(viewModel.ShowEnemyHeroesOnMinimap);

            var reloadedService = new FileAppSettingsService(tempSettingsFile);
            Assert.Equal(CloseBehavior.ExitApplication, reloadedService.Current.CloseBehavior);
            Assert.True(reloadedService.Current.RememberCloseChoice);
            Assert.True(reloadedService.Current.ShowEnemyHeroesOnMinimap);

            viewModel.MinimizeToTrayOnClose = true;
            viewModel.RememberCloseChoice = false;

            var reloadedAfterToggle = new FileAppSettingsService(tempSettingsFile);
            Assert.Equal(CloseBehavior.MinimizeToTray, reloadedAfterToggle.Current.CloseBehavior);
            Assert.False(reloadedAfterToggle.Current.RememberCloseChoice);
        }
        finally
        {
            if (File.Exists(tempSettingsFile))
            {
                File.Delete(tempSettingsFile);
            }
        }
    }

    [Fact]
    public void DotaMinimapIconProvider_DecodesOriginalVtexMinimapHeroIcons()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dota-vpk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Build a synthetic pak01_dir.vpk + pak01_001.vpk with one 32x32 BGRA8888 hero icon.
            const int width = 32;
            const int height = 32;
            const int nonStreamingSize = 0x80;
            const int pixelBytesCount = width * height * 4;
            var vtex = new byte[nonStreamingSize + pixelBytesCount];
            BitConverter.TryWriteBytes(vtex.AsSpan(0, 4), nonStreamingSize);
            BitConverter.TryWriteBytes(vtex.AsSpan(0x20, 4), 0x20); // dataOffset = 0x40
            BitConverter.TryWriteBytes(vtex.AsSpan(0x40 + 0x14, 2), (ushort)width);
            BitConverter.TryWriteBytes(vtex.AsSpan(0x40 + 0x16, 2), (ushort)height);
            vtex[0x40 + 0x1A] = 28; // BGRA8888
            vtex[nonStreamingSize + 3] = 0xFF;

            File.WriteAllBytes(Path.Combine(tempDir, "pak01_001.vpk"), vtex);

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(System.Text.Encoding.ASCII.GetBytes("panorama/images/heroes/icons\0"));
                bw.Write(System.Text.Encoding.ASCII.GetBytes("npc_dota_hero_abaddon_png\0"));
                bw.Write(0u); // crc
                bw.Write((ushort)0); // preload
                bw.Write((ushort)1); // archiveIndex = 001
                bw.Write(0u); // offset
                bw.Write((uint)vtex.Length); // length
                bw.Write((ushort)0xFFFF); // terminator
                bw.Write((byte)0); // end of files in directory
                File.WriteAllBytes(Path.Combine(tempDir, "pak01_dir.vpk"), ms.ToArray());
            }

            var provider = new DotaMinimapIconProvider(tempDir);
            Assert.True(provider.LoadedEntryCount >= 1);
            Assert.True(provider.TryGetHeroIcon("npc_dota_hero_abaddon", processId: null, out var iconByFullName));
            Assert.NotNull(iconByFullName);
            Assert.True(provider.TryGetHeroIcon("abaddon", processId: null, out var iconByShortName));
            Assert.NotNull(iconByShortName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class FixedProbe(VisibilitySnapshot snapshot) : IVisibilityProbe
    {
        public Task<VisibilitySnapshot> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class RecordingLogger : ISessionLogger
    {
        public string LogPath => "test.log";
        public VisibilitySnapshot? LastSnapshot { get; private set; }

        public void Log(string message)
        {
        }

        public void LogSnapshot(VisibilitySnapshot snapshot) => LastSnapshot = snapshot;
    }
}
