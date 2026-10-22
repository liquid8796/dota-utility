using System.IO;
using System.Reflection;
using DotaUtility.App.Services;
using DotaUtility.Application;
using DotaUtility.Domain;

namespace DotaUtility.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly VisibilityMonitor _monitor;
    private readonly ISessionLogger _logger;
    private readonly IAppSettingsService _settingsService;
    private readonly SynchronizationContext? _synchronizationContext;
    private VisibilitySnapshot _snapshot;
    private AppSettings _settings;
    private int _selectedTabIndex;
    private bool _disposed;

    public MainViewModel(
        VisibilityMonitor monitor,
        ISessionLogger logger,
        SynchronizationContext? synchronizationContext = null,
        IAppSettingsService? settingsService = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsService = settingsService ?? new FileAppSettingsService();
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _snapshot = monitor.Current;
        _settings = _settingsService.Current;
        _monitor.SnapshotChanged += OnSnapshotChanged;
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (_selectedTabIndex == value)
            {
                return;
            }

            _selectedTabIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDashboardTabSelected));
            OnPropertyChanged(nameof(IsSettingsTabSelected));
        }
    }

    public bool IsDashboardTabSelected
    {
        get => _selectedTabIndex == 0;
        set
        {
            if (value)
            {
                SelectedTabIndex = 0;
            }
        }
    }

    public bool IsSettingsTabSelected
    {
        get => _selectedTabIndex == 1;
        set
        {
            if (value)
            {
                SelectedTabIndex = 1;
            }
        }
    }

    public CloseBehavior CloseBehavior
    {
        get => _settings.CloseBehavior;
        set
        {
            if (_settings.CloseBehavior == value)
            {
                return;
            }

            _settings = _settings with { CloseBehavior = value };
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinimizeToTrayOnClose));
            OnPropertyChanged(nameof(ExitOnClose));
            OnPropertyChanged(nameof(CloseBehaviorSummaryText));
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settings.CloseBehavior == CloseBehavior.MinimizeToTray;
        set
        {
            if (value)
            {
                CloseBehavior = CloseBehavior.MinimizeToTray;
            }
        }
    }

    public bool ExitOnClose
    {
        get => _settings.CloseBehavior == CloseBehavior.ExitApplication;
        set
        {
            if (value)
            {
                CloseBehavior = CloseBehavior.ExitApplication;
            }
        }
    }

    public bool RememberCloseChoice
    {
        get => _settings.RememberCloseChoice;
        set
        {
            if (_settings.RememberCloseChoice == value)
            {
                return;
            }

            _settings = _settings with { RememberCloseChoice = value };
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseBehaviorSummaryText));
        }
    }

    public bool ShowEnemyHeroesOnMinimap
    {
        get => _settings.ShowEnemyHeroesOnMinimap;
        set
        {
            if (_settings.ShowEnemyHeroesOnMinimap == value)
            {
                return;
            }

            _settings = _settings with { ShowEnemyHeroesOnMinimap = value };
            _settingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public string CloseBehaviorSummaryText => _settings.RememberCloseChoice
        ? _settings.CloseBehavior == CloseBehavior.MinimizeToTray
            ? "Remembered: Minimize to System Tray automatically when clicking Close."
            : "Remembered: Exit application immediately when clicking Close."
        : "Ask every time when clicking the Close button.";

    public string SettingsPath => _settingsService.SettingsPath;

    public void ApplyCloseDecision(CloseBehavior behavior, bool rememberChoice)
    {
        _settings = _settings with
        {
            CloseBehavior = behavior,
            RememberCloseChoice = rememberChoice,
        };
        _settingsService.Save(_settings);
        OnPropertyChanged(nameof(CloseBehavior));
        OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        OnPropertyChanged(nameof(ExitOnClose));
        OnPropertyChanged(nameof(RememberCloseChoice));
        OnPropertyChanged(nameof(CloseBehaviorSummaryText));
    }

    public VisibilityState State => _snapshot.State;

    public bool IsAttached => _snapshot.IsAttached;

    public bool IsInEnemySentry => _snapshot.IsInEnemySentry;

    public string SentryStatusText => _snapshot.IsInEnemySentry
        ? "IN ENEMY SENTRY RANGE"
        : "CLEAR";

    public string StatusHeadline => _snapshot.State switch
    {
        VisibilityState.Visible => "VISIBLE",
        VisibilityState.Hidden => "HIDDEN",
        _ when _snapshot.IsAttached => "SYNCING",
        _ => "WAITING",
    };

    public string StatusEyebrow => _snapshot.State switch
    {
        VisibilityState.Visible when _snapshot.IsInEnemySentry => "ENEMY VISION + SENTRY TRUE SIGHT",
        VisibilityState.Visible => "ENEMY VISION DETECTED",
        VisibilityState.Hidden when _snapshot.IsInEnemySentry => "ENEMY SENTRY RANGE DETECTED",
        VisibilityState.Hidden => "ENEMY VISION CLEAR",
        _ => "VISIBILITY STATUS",
    };

    public string StatusDetail => _snapshot.Detail;

    public string ConnectionText => _snapshot.IsAttached ? "CONNECTED" : "DISCONNECTED";

    public string ProcessText => _snapshot.ProcessId is int processId ? $"PID {processId}" : "—";

    public string LocalTeamText => FormatTeam(_snapshot.LocalTeam);

    public string EnemyTeamText => FormatTeam(_snapshot.EnemyTeam);

    public string VisibilityMaskText => _snapshot.RawVisibilityMask is int mask
        ? $"0x{unchecked((uint)mask):X8}"
        : "—";

    public string HeroAddressText => _snapshot.HeroAddress is ulong address
        ? $"0x{address:X16}"
        : "—";

    public string LastUpdatedText => _snapshot.ObservedAt.ToLocalTime().ToString("HH:mm:ss.fff");

    public string LogPath => _logger.LogPath;

    public string VersionText
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "v—" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logger.Log($"Dota Utility {VersionText} starting. Read-only visibility probe configured with strict audited-build gating. Log: {_logger.LogPath}");
        return _monitor.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.SnapshotChanged -= OnSnapshotChanged;
        await _monitor.StopAsync().ConfigureAwait(false);
        _logger.Log("Dota Utility stopped.");
    }

    private void OnSnapshotChanged(object? sender, VisibilitySnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            ApplySnapshot(snapshot);
            return;
        }

        _synchronizationContext.Post(
            static state =>
            {
                var (viewModel, value) = ((MainViewModel ViewModel, VisibilitySnapshot Snapshot))state!;
                viewModel.ApplySnapshot(value);
            },
            (this, snapshot));
    }

    private void ApplySnapshot(VisibilitySnapshot snapshot)
    {
        // A SynchronizationContext.Post callback can run after DisposeAsync
        // unsubscribes. It must not mutate or log a disposed dashboard.
        // A queued UI callback may run after a newer monitor snapshot was
        // published (especially across StopAsync). Never replay old state.
        if (_disposed || !ReferenceEquals(snapshot, _monitor.Current))
        {
            return;
        }

        _snapshot = snapshot;
        try
        {
            _logger.LogSnapshot(snapshot);
        }
        catch (IOException)
        {
            // Disk errors are diagnostic-only; the UI must still update.
        }
        catch (UnauthorizedAccessException)
        {
            // A locked or unwritable log must not leave stale UI state.
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsAttached));
        OnPropertyChanged(nameof(IsInEnemySentry));
        OnPropertyChanged(nameof(SentryStatusText));
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusEyebrow));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(ProcessText));
        OnPropertyChanged(nameof(LocalTeamText));
        OnPropertyChanged(nameof(EnemyTeamText));
        OnPropertyChanged(nameof(VisibilityMaskText));
        OnPropertyChanged(nameof(HeroAddressText));
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private static string FormatTeam(int? team) => team switch
    {
        2 => "Radiant",
        3 => "Dire",
        int value => $"Team {value}",
        _ => "Unknown",
    };
}
