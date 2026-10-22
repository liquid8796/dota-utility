using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DotaUtility.Dota2;

namespace DotaUtility.App;

public partial class VisionIndicatorWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x0000_0020L;
    private const long WsExToolWindow = 0x0000_0080L;
    private const long WsExNoActivate = 0x0800_0000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const double DpiBaseline = 96d;

    // Aligns the hero's projected ground origin to Y=260 inside the 360-DIP window
    // so the feet ring sits at Y=270 (+10 DIPs) and the overhead Sentry particle sits at Y=54 (-206 DIPs).
    private const double WindowCenterOffsetFromHeroOriginDip = -80d;

    private static readonly nint HwndTopmost = new(-1);

    private nint _handle;
    private nint _lastDotaWindowHandle;
    private double _cachedScale = 1d;
    private int _lastLeft = int.MinValue;
    private int _lastTop = int.MinValue;
    private int _lastPixelWidth;
    private int _lastPixelHeight;
    private bool _lastShowRing = true;
    private bool _lastShowSentry;
    private bool _isShown;

    public VisionIndicatorWindow()
    {
        InitializeComponent();
    }

    public void InitializeOverlay()
    {
        if (_handle != 0)
        {
            return;
        }

        _handle = new WindowInteropHelper(this).EnsureHandle();
        var extendedStyle = NativeMethods.GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        _ = NativeMethods.SetWindowLongPtr(
            _handle,
            GwlExStyle,
            new nint(extendedStyle | WsExTransparent | WsExToolWindow | WsExNoActivate));
        HideIndicator();
    }

    public void ShowIndicator(
        nint dotaWindowHandle,
        int clientScreenLeft,
        int clientScreenTop,
        DotaScreenPoint clientPoint,
        bool showVisibilityRing = true,
        bool showSentryParticle = false)
    {
        InitializeOverlay();

        if (showVisibilityRing != _lastShowRing)
        {
            Indicator.Visibility = showVisibilityRing ? Visibility.Visible : Visibility.Collapsed;
            _lastShowRing = showVisibilityRing;
        }

        if (showSentryParticle != _lastShowSentry)
        {
            SentryIndicator.Visibility = showSentryParticle ? Visibility.Visible : Visibility.Collapsed;
            _lastShowSentry = showSentryParticle;
        }

        if (dotaWindowHandle != _lastDotaWindowHandle)
        {
            var dpi = NativeMethods.GetDpiForWindow(dotaWindowHandle);
            _cachedScale = dpi > 0 ? dpi / DpiBaseline : 1d;
            _lastDotaWindowHandle = dotaWindowHandle;
        }

        var scale = _cachedScale;
        var pixelWidth = Math.Max(1, (int)Math.Round(Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(Height * scale));
        var centerX = clientScreenLeft + clientPoint.X;
        var centerY = clientScreenTop + clientPoint.Y + (WindowCenterOffsetFromHeroOriginDip * scale);
        var left = (int)Math.Round(centerX - (pixelWidth / 2d));
        var top = (int)Math.Round(centerY - (pixelHeight / 2d));

        if (!IsVisible)
        {
            Show();
            _isShown = false;
        }

        if (_isShown
            && pixelWidth == _lastPixelWidth
            && pixelHeight == _lastPixelHeight
            && Math.Abs(left - _lastLeft) <= 1
            && Math.Abs(top - _lastTop) <= 1)
        {
            return;
        }

        var flags = !_isShown || pixelWidth != _lastPixelWidth || pixelHeight != _lastPixelHeight
            ? SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder
            : SwpNoActivate | SwpNoZOrder | SwpNoSize | SwpNoOwnerZOrder;

        _ = NativeMethods.SetWindowPos(
            _handle,
            HwndTopmost,
            left,
            top,
            pixelWidth,
            pixelHeight,
            flags);

        _lastLeft = left;
        _lastTop = top;
        _lastPixelWidth = pixelWidth;
        _lastPixelHeight = pixelHeight;
        _isShown = true;
    }

    public void HideIndicator()
    {
        _isShown = false;
        _lastLeft = int.MinValue;
        _lastTop = int.MinValue;
        if (IsVisible)
        {
            Hide();
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static partial nint GetWindowLongPtr(nint windowHandle, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

        [LibraryImport("user32.dll")]
        internal static partial uint GetDpiForWindow(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
