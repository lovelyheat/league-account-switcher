using System.Runtime.InteropServices;
using LeagueAccountSwitcher.Core.Models;
using Microsoft.UI.Dispatching;

namespace LeagueAccountSwitcher.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint WmUser = 0x0400;
    private const uint WmTrayCallback = WmUser + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private const uint MfString = 0x00000000;
    private const uint MfChecked = 0x00000008;
    private const uint MfDisabled = 0x00000002;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmBottomAlign = 0x0020;

    private const uint CmdOpen = 1;
    private const uint CmdExit = 2;
    private const uint CmdSwitchBase = 100;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action _onOpen;
    private readonly Action<AccountRecord> _onSwitchAccount;
    private readonly Action _onExit;
    private readonly Func<IReadOnlyList<AccountRecord>> _getAccounts;
    private readonly Func<string?> _getActiveAccountId;
    private readonly Func<bool> _canSwitch;

    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly uint _taskbarCreatedMessage;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    public TrayIconService(
        DispatcherQueue dispatcher,
        string iconPath,
        Action onOpen,
        Action<AccountRecord> onSwitchAccount,
        Action onExit,
        Func<IReadOnlyList<AccountRecord>> getAccounts,
        Func<string?> getActiveAccountId,
        Func<bool> canSwitch)
    {
        _dispatcher = dispatcher;
        _onOpen = onOpen;
        _onSwitchAccount = onSwitchAccount;
        _onExit = onExit;
        _getAccounts = getAccounts;
        _getActiveAccountId = getActiveAccountId;
        _canSwitch = canSwitch;

        _wndProc = WindowProc;
        _className = $"LeagueAccountSwitcher_Tray_{Guid.NewGuid():N}";
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        CreateMessageWindow();
        LoadIcon(iconPath);
        AddOrUpdateTrayIcon(add: true, "League Account Switcher");
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed || !_added)
            return;

        AddOrUpdateTrayIcon(add: false, text);
    }

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };

        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, _className, "LeagueAccountSwitcherTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private void LoadIcon(string iconPath)
    {
        if (!File.Exists(iconPath))
            return;

        var cx = GetSystemMetrics(SmCxSmIcon);
        var cy = GetSystemMetrics(SmCySmIcon);
        _hIcon = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, cx, cy, LrLoadFromFile);
    }

    private void AddOrUpdateTrayIcon(bool add, string tooltip)
    {
        if (_hwnd == IntPtr.Zero || _hIcon == IntPtr.Zero)
            return;

        var trimmedTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = trimmedTip,
        };

        if (Shell_NotifyIconW(add ? NimAdd : NimModify, ref nid))
            _added = true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            AddOrUpdateTrayIcon(add: true, "League Account Switcher");
            return IntPtr.Zero;
        }

        if (msg == WmTrayCallback)
        {
            var eventId = (uint)(lParam.ToInt64() & 0xFFFF);
            if (eventId is WmLButtonUp or WmLButtonDblClk)
            {
                _dispatcher.TryEnqueue(() => _onOpen());
            }
            else if (eventId is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MfString, CmdOpen, "Open League Account Switcher");
            SetMenuDefaultItem(menu, CmdOpen, 0);

            var accounts = _getAccounts();
            var activeId = _getActiveAccountId();
            var canSwitch = _canSwitch();

            if (accounts.Count > 0)
            {
                AppendMenuW(menu, MfSeparator, 0, null);
                var count = Math.Min(accounts.Count, 20);
                for (var i = 0; i < count; i++)
                {
                    var acc = accounts[i];
                    var isActive = acc.Id == activeId;
                    var flags = MfString | (isActive ? MfChecked : 0u) | (canSwitch ? 0u : (MfDisabled | MfGrayed));
                    var regionTag = string.Equals(acc.Region, "Unknown", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : $" [{acc.Region}]";
                    var rankTag = acc.HasRankData || acc.RankedSolo.IsRanked
                        ? $" — {acc.RankedSolo.FormattedTierAndDivision}"
                        : string.Empty;
                    var label = $"{acc.DisplayName}{regionTag}{rankTag}".Replace("&", "&&");
                    AppendMenuW(menu, flags, CmdSwitchBase + (uint)i, label);
                }
            }

            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdExit, "Exit");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmBottomAlign, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            if (selected == CmdOpen)
            {
                _dispatcher.TryEnqueue(() => _onOpen());
            }
            else if (selected == CmdExit)
            {
                _dispatcher.TryEnqueue(() => _onExit());
            }
            else if (selected >= CmdSwitchBase)
            {
                var idx = (int)(selected - CmdSwitchBase);
                if (idx >= 0 && idx < accounts.Count)
                {
                    var chosen = accounts[idx];
                    _dispatcher.TryEnqueue(() => _onSwitchAccount(chosen));
                }
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_added && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NimDelete, ref nid);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
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
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
