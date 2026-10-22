using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace DotaUtility.App.Services;

public sealed partial class SystemTrayService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 0x44;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0x0000_0000;
    private const uint NimModify = 0x0000_0001;
    private const uint NimDelete = 0x0000_0002;

    private const uint NifMessage = 0x0000_0001;
    private const uint NifIcon = 0x0000_0002;
    private const uint NifTip = 0x0000_0004;
    private const uint NifInfo = 0x0000_0010;
    private const uint NiifInfo = 0x0000_0001;
    private const uint NiifNoSound = 0x0000_0010;

    private const uint TrayIconId = 1001;

    private readonly Action _restoreDashboardAction;
    private readonly Action _openSettingsAction;
    private readonly Action _exitApplicationAction;
    private readonly ContextMenu _contextMenu;

    private HwndSource? _hwndSource;
    private nint _windowHandle;
    private nint _iconHandle;
    private bool _iconAdded;
    private bool _balloonShownOnce;
    private bool _disposed;

    public SystemTrayService(
        Action restoreDashboardAction,
        Action openSettingsAction,
        Action exitApplicationAction)
    {
        _restoreDashboardAction = restoreDashboardAction ?? throw new ArgumentNullException(nameof(restoreDashboardAction));
        _openSettingsAction = openSettingsAction ?? throw new ArgumentNullException(nameof(openSettingsAction));
        _exitApplicationAction = exitApplicationAction ?? throw new ArgumentNullException(nameof(exitApplicationAction));
        _contextMenu = BuildContextMenu();
    }

    public void Initialize(Window ownerWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ownerWindow);

        if (_iconAdded)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(ownerWindow).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);

        _iconHandle = CreateTrayIconHandle();
        var data = CreateBaseNotifyIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        _iconAdded = NativeMethods.Shell_NotifyIconW(NimAdd, ref data);
    }

    public void ShowMinimizedNotification()
    {
        if (_disposed || !_iconAdded || _balloonShownOnce)
        {
            return;
        }

        _balloonShownOnce = true;
        var data = CreateBaseNotifyIconData();
        data.uFlags = NifInfo;
        data.szInfoTitle = "Dota Utility";
        data.szInfo = "Dota Utility is still running in the System Tray. Double-click the icon to open.";
        data.dwInfoFlags = NiifInfo | NiifNoSound;
        _ = NativeMethods.Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _contextMenu.IsOpen = false;

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        if (_iconAdded && _windowHandle != 0)
        {
            var data = CreateBaseNotifyIconData();
            _ = NativeMethods.Shell_NotifyIconW(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_iconHandle != 0)
        {
            _ = NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }

        GC.SuppressFinalize(this);
    }

    private NotifyIconData CreateBaseNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _windowHandle,
        uID = TrayIconId,
        uCallbackMessage = WmTrayCallback,
        hIcon = _iconHandle,
        szTip = "Dota Utility — Read-Only Vision Monitor",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmTrayCallback)
        {
            return 0;
        }

        var eventId = unchecked((int)lParam.ToInt64() & 0xFFFF);
        switch (eventId)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                _contextMenu.IsOpen = false;
                _restoreDashboardAction();
                handled = true;
                break;

            case WmRButtonUp:
            case WmContextMenu:
                ShowContextMenu();
                handled = true;
                break;
        }

        return 0;
    }

    private void ShowContextMenu()
    {
        if (_windowHandle != 0)
        {
            _ = NativeMethods.SetForegroundWindow(_windowHandle);
        }

        _contextMenu.Placement = PlacementMode.MousePoint;
        _contextMenu.IsOpen = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(245, 247, 255)),
            Padding = new Thickness(6),
        };

        var openItem = new MenuItem
        {
            Header = "Open Dota Utility",
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(8, 6, 12, 6),
        };
        openItem.Click += (_, _) => _restoreDashboardAction();

        var settingsItem = new MenuItem
        {
            Header = "Settings...",
            Padding = new Thickness(8, 6, 12, 6),
        };
        settingsItem.Click += (_, _) => _openSettingsAction();

        var separator = new Separator
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Margin = new Thickness(4, 4, 4, 4),
        };

        var exitItem = new MenuItem
        {
            Header = "Exit Dota Utility",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 92, 120)),
            Padding = new Thickness(8, 6, 12, 6),
        };
        exitItem.Click += (_, _) => _exitApplicationAction();

        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(separator);
        menu.Items.Add(exitItem);
        return menu;
    }

    private static nint CreateTrayIconHandle()
    {
        const int size = 32;
        var bmi = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = size,
            biHeight = -size, // top-down DIB
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        var hdc = NativeMethods.GetDC(0);
        var colorBitmap = NativeMethods.CreateDIBSection(hdc, ref bmi, 0, out var bits, 0, 0);
        _ = NativeMethods.ReleaseDC(0, hdc);

        if (colorBitmap == 0 || bits == 0)
        {
            return 0;
        }

        var maskBitmap = NativeMethods.CreateBitmap(size, size, 1, 1, 0);
        try
        {
            const float center = (size - 1) / 2f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var dist = MathF.Sqrt((dx * dx) + (dy * dy));

                    uint bgra = 0;
                    if (dist <= 14.5f)
                    {
                        // Rounded dark badge background
                        bgra = 0xFF141828;
                    }

                    if (dist >= 9.0f && dist <= 12.2f)
                    {
                        // Dota 2 emerald particle ring
                        bgra = 0xFF4FE3A4;
                    }
                    else if (dist <= 4.0f)
                    {
                        // Inner vision core dot
                        bgra = 0xFF8C6EFF;
                    }

                    var byteOffset = ((y * size) + x) * sizeof(uint);
                    Marshal.WriteInt32(bits, byteOffset, unchecked((int)bgra));
                }
            }

            var iconInfo = new IconInfo
            {
                fIcon = 1,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };
            return NativeMethods.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (colorBitmap != 0)
            {
                _ = NativeMethods.DeleteObject(colorBitmap);
            }

            if (maskBitmap != 0)
            {
                _ = NativeMethods.DeleteObject(maskBitmap);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    private static partial class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(nint hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint CreateIconIndirect(ref IconInfo iconInfo);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(nint hIcon);

        [LibraryImport("user32.dll")]
        internal static partial nint GetDC(nint hWnd);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(nint hWnd, nint hDC);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateDIBSection(
            nint hdc,
            ref BitmapInfoHeader pbmi,
            uint usage,
            out nint ppvBits,
            nint hSection,
            uint offset);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateBitmap(
            int nWidth,
            int nHeight,
            uint nPlanes,
            uint nBitCount,
            nint lpBits);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(nint hObject);
    }
}
