using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DotaUtility.App.Services;
using DotaUtility.Domain;

namespace DotaUtility.App;

public partial class MinimapOverlayWindow : Window
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

    private const float WorldMin = -8192f;
    private const float WorldSpan = 16384f;
    private const double MinimapCanvasSizeDip = 244d;
    private const double MarkerSizeDip = 28d;
    private const int MaxMarkers = 12;

    private static readonly nint HwndTopmost = new(-1);
    private static readonly Brush FallbackFillBrush = new SolidColorBrush(Color.FromArgb(230, 210, 36, 60));
    private static readonly Brush EnemyRingBrush = new SolidColorBrush(Color.FromArgb(235, 255, 62, 88));
    private static readonly Brush FogEnemyRingBrush = new SolidColorBrush(Color.FromArgb(180, 255, 130, 148));
    private static readonly Brush TextForegroundBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    private readonly DotaMinimapIconProvider _iconProvider;
    private readonly MarkerSlot[] _slots = new MarkerSlot[MaxMarkers];
    private nint _handle;
    private int _lastLeft = int.MinValue;
    private int _lastTop = int.MinValue;
    private int _lastPixelSize;
    private bool _isShown;

    public MinimapOverlayWindow(DotaMinimapIconProvider? iconProvider = null)
    {
        InitializeComponent();
        _iconProvider = iconProvider ?? new DotaMinimapIconProvider();

        for (var i = 0; i < MaxMarkers; i++)
        {
            var badge = new Grid
            {
                Width = MarkerSizeDip,
                Height = MarkerSizeDip,
                Visibility = Visibility.Collapsed,
            };

            var ring = new Ellipse
            {
                Width = MarkerSizeDip - 2d,
                Height = MarkerSizeDip - 2d,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Brushes.Transparent,
                Stroke = EnemyRingBrush,
                StrokeThickness = 1.5,
            };

            var iconImage = new Image
            {
                Width = MarkerSizeDip,
                Height = MarkerSizeDip,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);

            var fallbackLabel = new TextBlock
            {
                Text = string.Empty,
                FontSize = 7.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextForegroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            badge.Children.Add(ring);
            badge.Children.Add(iconImage);
            badge.Children.Add(fallbackLabel);
            MinimapCanvas.Children.Add(badge);
            _slots[i] = new MarkerSlot(badge, ring, iconImage, fallbackLabel);
        }
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
        HideOverlay();
    }

    public void ShowMarkers(
        int? processId,
        int clientScreenLeft,
        int clientScreenTop,
        int clientPixelWidth,
        int clientPixelHeight,
        IReadOnlyList<EnemyHeroMinimapMarker> markers)
    {
        InitializeOverlay();

        if (markers.Count == 0 || clientPixelWidth <= 0 || clientPixelHeight <= 0)
        {
            HideOverlay();
            return;
        }

        var hudScale = clientPixelHeight / 1080d;
        var pixelSize = Math.Max(64, (int)Math.Round(MinimapCanvasSizeDip * hudScale));
        var marginPixels = (int)Math.Round(12d * hudScale);
        var left = clientScreenLeft + marginPixels;
        var top = clientScreenTop + clientPixelHeight - pixelSize - marginPixels;

        var visibleCount = Math.Min(markers.Count, MaxMarkers);
        for (var i = 0; i < MaxMarkers; i++)
        {
            var slot = _slots[i];
            if (i >= visibleCount)
            {
                if (slot.Container.Visibility != Visibility.Collapsed)
                {
                    slot.Container.Visibility = Visibility.Collapsed;
                }

                continue;
            }

            var marker = markers[i];
            var u = Math.Clamp((marker.WorldX - WorldMin) / WorldSpan, 0.04f, 0.96f);
            var v = 1f - Math.Clamp((marker.WorldY - WorldMin) / WorldSpan, 0.04f, 0.96f);
            var canvasX = (u * MinimapCanvasSizeDip) - (MarkerSizeDip / 2d);
            var canvasY = (v * MinimapCanvasSizeDip) - (MarkerSizeDip / 2d);

            Canvas.SetLeft(slot.Container, canvasX);
            Canvas.SetTop(slot.Container, canvasY);

            if (!string.Equals(slot.CurrentHeroName, marker.HeroName, StringComparison.OrdinalIgnoreCase))
            {
                slot.CurrentHeroName = marker.HeroName;
                if (_iconProvider.TryGetHeroIcon(marker.HeroName, processId, out var heroIcon) && heroIcon is not null)
                {
                    slot.IconImage.Source = heroIcon;
                    slot.IconImage.Visibility = Visibility.Visible;
                    slot.FallbackLabel.Visibility = Visibility.Collapsed;
                    slot.Ring.Fill = Brushes.Transparent;
                }
                else
                {
                    slot.IconImage.Source = null;
                    slot.IconImage.Visibility = Visibility.Collapsed;
                    slot.FallbackLabel.Text = FormatFallbackShortTag(marker.HeroName);
                    slot.FallbackLabel.Visibility = Visibility.Visible;
                    slot.Ring.Fill = FallbackFillBrush;
                }
            }

            slot.Ring.Stroke = marker.IsLive ? EnemyRingBrush : FogEnemyRingBrush;
            slot.Container.Opacity = marker.IsLive ? 1.0 : 0.72;

            if (slot.Container.Visibility != Visibility.Visible)
            {
                slot.Container.Visibility = Visibility.Visible;
            }
        }

        if (!IsVisible)
        {
            Show();
            _isShown = false;
        }

        if (_isShown
            && pixelSize == _lastPixelSize
            && Math.Abs(left - _lastLeft) <= 1
            && Math.Abs(top - _lastTop) <= 1)
        {
            return;
        }

        var flags = !_isShown || pixelSize != _lastPixelSize
            ? SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder
            : SwpNoActivate | SwpNoZOrder | SwpNoSize | SwpNoOwnerZOrder;

        _ = NativeMethods.SetWindowPos(
            _handle,
            HwndTopmost,
            left,
            top,
            pixelSize,
            pixelSize,
            flags);

        _lastLeft = left;
        _lastTop = top;
        _lastPixelSize = pixelSize;
        _isShown = true;
    }

    public void HideOverlay()
    {
        _isShown = false;
        _lastLeft = int.MinValue;
        _lastTop = int.MinValue;
        if (IsVisible)
        {
            Hide();
        }
    }

    private static string FormatFallbackShortTag(string heroName)
    {
        const string Prefix = "npc_dota_hero_";
        var clean = heroName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? heroName[Prefix.Length..]
            : heroName;
        clean = clean.Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return "HERO";
        }

        return clean.Length <= 3 ? clean.ToUpperInvariant() : clean[..3].ToUpperInvariant();
    }

    private sealed class MarkerSlot(Grid container, Ellipse ring, Image iconImage, TextBlock fallbackLabel)
    {
        public Grid Container { get; } = container;
        public Ellipse Ring { get; } = ring;
        public Image IconImage { get; } = iconImage;
        public TextBlock FallbackLabel { get; } = fallbackLabel;
        public string? CurrentHeroName { get; set; }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static partial nint GetWindowLongPtr(nint windowHandle, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

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
