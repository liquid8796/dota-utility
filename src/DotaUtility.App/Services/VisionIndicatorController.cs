using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DotaUtility.Application;
using DotaUtility.Domain;
using DotaUtility.Dota2;

namespace DotaUtility.App.Services;

public sealed partial class VisionIndicatorController : IDisposable
{
    private const string EngineBridgeDetailPrefix = "Dota engine FoW bridge:";

    private readonly VisibilityMonitor _monitor;
    private readonly DotaHeroScreenProjector _projector;
    private readonly VisionIndicatorWindow _window;
    private readonly MinimapOverlayWindow? _minimapWindow;
    private readonly IAppSettingsService? _settingsService;
    private readonly DispatcherTimer _timer;

    private int? _windowProcessId;
    private nint _dotaWindowHandle;
    private bool _disposed;

    public VisionIndicatorController(
        VisibilityMonitor monitor,
        DotaHeroScreenProjector projector,
        VisionIndicatorWindow window,
        MinimapOverlayWindow? minimapWindow = null,
        IAppSettingsService? settingsService = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _minimapWindow = minimapWindow;
        _settingsService = settingsService;
        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.InitializeOverlay();
        _minimapWindow?.InitializeOverlay();
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _window.HideIndicator();
        _window.Close();
        _minimapWindow?.HideOverlay();
        _minimapWindow?.Close();
        GC.SuppressFinalize(this);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = _monitor.Current;
            var engineBridgeActive = snapshot.Detail.StartsWith(EngineBridgeDetailPrefix, StringComparison.Ordinal);

            if (snapshot.ProcessId is not int processId
                || !TryGetDotaWindow(processId, out var dotaWindow)
                || NativeMethods.GetForegroundWindow() != dotaWindow
                || !TryGetClientBounds(dotaWindow, out var bounds))
            {
                _window.HideIndicator();
                _minimapWindow?.HideOverlay();
                return;
            }

            if (_minimapWindow is not null
                && !engineBridgeActive
                && _settingsService?.Current.ShowEnemyHeroesOnMinimap == true
                && snapshot.EnemyHeroes is { Count: > 0 } enemyHeroes)
            {
                _minimapWindow.ShowMarkers(
                    processId,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    enemyHeroes);
            }
            else
            {
                _minimapWindow?.HideOverlay();
            }

            if (engineBridgeActive)
            {
                _window.HideIndicator();
                return;
            }

            var showVisibilityRing = snapshot.State == VisibilityState.Visible;
            var showSentryParticle = snapshot.IsInEnemySentry;

            if ((!showVisibilityRing && !showSentryParticle)
                || snapshot.HeroAddress is not ulong heroAddress
                || !_projector.TryProject(
                    heroAddress,
                    bounds.Width,
                    bounds.Height,
                    out var screenPoint))
            {
                _window.HideIndicator();
                return;
            }

            _window.ShowIndicator(
                dotaWindow,
                bounds.Left,
                bounds.Top,
                screenPoint,
                showVisibilityRing,
                showSentryParticle);
        }
        catch (ArgumentException)
        {
            InvalidateDotaWindow();
            _window.HideIndicator();
            _minimapWindow?.HideOverlay();
        }
        catch (InvalidOperationException)
        {
            InvalidateDotaWindow();
            _window.HideIndicator();
            _minimapWindow?.HideOverlay();
        }
    }

    private bool TryGetDotaWindow(int processId, out nint windowHandle)
    {
        if (_windowProcessId == processId
            && _dotaWindowHandle != 0
            && NativeMethods.IsWindow(_dotaWindowHandle))
        {
            windowHandle = _dotaWindowHandle;
            return true;
        }

        InvalidateDotaWindow();
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            var candidate = process.MainWindowHandle;
            if (candidate == 0 || !NativeMethods.IsWindow(candidate))
            {
                windowHandle = 0;
                return false;
            }

            _windowProcessId = processId;
            _dotaWindowHandle = candidate;
            windowHandle = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            windowHandle = 0;
            return false;
        }
        catch (InvalidOperationException)
        {
            windowHandle = 0;
            return false;
        }
    }

    private static bool TryGetClientBounds(nint windowHandle, out ClientBounds bounds)
    {
        bounds = default;
        if (!NativeMethods.GetClientRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var topLeft = new NativePoint();
        if (!NativeMethods.ClientToScreen(windowHandle, ref topLeft))
        {
            return false;
        }

        bounds = new ClientBounds(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    private void InvalidateDotaWindow()
    {
        _windowProcessId = null;
        _dotaWindowHandle = 0;
    }

    private readonly record struct ClientBounds(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetClientRect(nint windowHandle, out NativeRect rect);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClientToScreen(nint windowHandle, ref NativePoint point);
    }
}
