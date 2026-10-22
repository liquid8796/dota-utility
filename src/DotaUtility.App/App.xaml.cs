using DotaUtility.App.Services;
using DotaUtility.App.ViewModels;
using DotaUtility.Application;
using DotaUtility.Dota2;
using DotaUtility.Memory;

namespace DotaUtility.App;

public partial class App : System.Windows.Application
{
    private Win32ProcessMemoryAccessor? _memory;
    private MainViewModel? _viewModel;
    private VisionIndicatorController? _visionIndicatorController;
    private SystemTrayService? _systemTrayService;
    private LocalLobbyVisibilityBridge? _visibilityBridge;
    private VisibilityRelayHost? _relayHost;
    private VisibilityRelayClient? _relayClient;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _memory = new Win32ProcessMemoryAccessor();
        var logger = new FileSessionLogger();
        var settingsService = new FileAppSettingsService();
        var addressResolver = new DotaAddressResolver(
            _memory,
            resolveNetworkGameClient: false);
        var buildCompatibility = new DotaBuildCompatibilityGate(_memory);
        IVisibilityProbe probe = new DotaUtilityProbe(
            _memory,
            addressResolver,
            DotaOffsets.Current,
            buildCompatibility);

        var compatRelayEnv = Environment.GetEnvironmentVariable("DOTAUTILITY_ENABLE_COMPAT_RELAY");
        var compatibilityRelayEnabled = !string.Equals(
                compatRelayEnv,
                "0",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                compatRelayEnv,
                "false",
                StringComparison.OrdinalIgnoreCase);

        if (compatibilityRelayEnabled)
        {
            var overrideProviders = new List<IVisibilityOverrideProvider>();

            _visibilityBridge = new LocalLobbyVisibilityBridge(
                isEnemyMinimapEnabled: () => settingsService.Current.ShowEnemyHeroesOnMinimap);
            try
            {
                _visibilityBridge.Start();
                overrideProviders.Add(_visibilityBridge);
                logger.Log($"Dota engine visibility bridge listening on 127.0.0.1:{_visibilityBridge.Port}.");
            }
            catch (Exception exception)
            {
                logger.Log($"Dota engine visibility bridge could not start: {exception.Message}");
            }

            _relayClient = new VisibilityRelayClient();
            try
            {
                _relayClient.Start();
                overrideProviders.Add(_relayClient);
                var manualRelay = Environment.GetEnvironmentVariable(
                    VisibilityRelayClient.ManualHostEnvironmentVariable);
                logger.Log(string.IsNullOrWhiteSpace(manualRelay)
                    ? "Remote host visibility relay client enabled with LAN discovery."
                    : $"Remote host visibility relay client pinned to {manualRelay}.");
            }
            catch (Exception exception)
            {
                logger.Log($"Remote host visibility relay client could not start: {exception.Message}");
            }

            if (_visibilityBridge is not null)
            {
                _relayHost = new VisibilityRelayHost(_visibilityBridge);
                try
                {
                    _relayHost.Start();
                    logger.Log(
                        $"Read-only host visibility relay listening on 0.0.0.0:{_relayHost.RelayPort}; " +
                        $"LAN discovery UDP/{VisibilityRelayHost.DefaultDiscoveryPort}.");
                }
                catch (Exception exception)
                {
                    logger.Log($"Host visibility relay could not start: {exception.Message}");
                }
            }

            if (overrideProviders.Count > 0)
            {
                probe = new VisibilityOverrideProbe(
                    probe,
                    new CompositeVisibilityOverrideProvider(overrideProviders.ToArray()),
                    TimeSpan.FromSeconds(2),
                    allowMemoryFallback: true);
            }
        }
        else
        {
            logger.Log("Optional VScript/host-relay compatibility is disabled; the read-only memory path remains subject to strict audited-build gating.");
        }

        var monitor = new VisibilityMonitor(probe, TimeSpan.FromMilliseconds(150));
        _viewModel = new MainViewModel(monitor, logger, SynchronizationContext.Current, settingsService);

        var window = new MainWindow
        {
            DataContext = _viewModel,
        };

        MainWindow = window;
        window.Show();

        _systemTrayService = new SystemTrayService(
            restoreDashboardAction: () => window.RestoreFromTray(selectSettingsTab: false),
            openSettingsAction: () => window.RestoreFromTray(selectSettingsTab: true),
            exitApplicationAction: () => window.ExitApplication());
        window.AttachSystemTray(_systemTrayService);

        var screenProjector = new DotaHeroScreenProjector(
            _memory,
            DotaOffsets.Current,
            buildCompatibility);
        _visionIndicatorController = new VisionIndicatorController(
            monitor,
            screenProjector,
            new VisionIndicatorWindow(),
            new MinimapOverlayWindow(),
            settingsService);
        _visionIndicatorController.Start();

        _ = StartMonitorAsync(_viewModel, logger);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _systemTrayService?.Dispose();
        _visionIndicatorController?.Dispose();

        if (_viewModel is not null)
        {
            try
            {
                _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Exit should not be blocked by a best-effort monitor shutdown.
            }
        }

        if (_relayClient is not null)
        {
            try
            {
                _relayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Exit should not be blocked by a best-effort relay-client shutdown.
            }
        }

        if (_relayHost is not null)
        {
            try
            {
                _relayHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Exit should not be blocked by a best-effort relay-host shutdown.
            }
        }

        if (_visibilityBridge is not null)
        {
            try
            {
                _visibilityBridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Exit should not be blocked by a best-effort bridge shutdown.
            }
        }

        _memory?.Dispose();
        base.OnExit(e);
    }

    private static async Task StartMonitorAsync(MainViewModel viewModel, ISessionLogger logger)
    {
        try
        {
            await viewModel.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            logger.Log($"Monitor startup failed: {exception}");
        }
    }
}
