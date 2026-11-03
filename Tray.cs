using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ScrcpyZ;

/// <summary>
/// Ikona u system trayu (pored sata) preko Win32 Shell_NotifyIcon — bez WinForms-a.
/// Levi klik → Activate, desni klik → Menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const int WM_TRAY = 0x8000 + 77;          // WM_APP + n
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);

    readonly HwndSource _src;
    readonly IntPtr _icon;
    readonly bool _ownIcon;
    readonly int _taskbarCreated;
    bool _added;
    string _tip = "ScrcpyZ";

    public event Action? Activate;
    public ContextMenu? Menu { get; set; }

    public TrayIcon()
    {
        // Skriveni prozor koji prima poruke od ikone (i "TaskbarCreated" kad se Explorer restartuje)
        _src = new HwndSource(new HwndSourceParameters("ScrcpyZTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(Hook);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var small = new IntPtr[1];
        var exe = Environment.ProcessPath;
        if (exe != null && ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
        {
            _icon = small[0];
            _ownIcon = true;
        }
        else
        {
            _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        }
    }

    public bool Visible => _added;

    public void Show(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        var d = Data();
        _added = _added
            ? Shell_NotifyIcon(NIM_MODIFY, ref d) || Shell_NotifyIcon(NIM_ADD, ref d)
            : Shell_NotifyIcon(NIM_ADD, ref d);
    }

    public void Remove()
    {
        if (!_added) return;
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _added = false;
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _src.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = ""
    };

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK) Activate?.Invoke();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            handled = true;
        }
        else if (msg == _taskbarCreated && _added)
        {
            _added = false;   // Explorer je restartovan — ikonu treba ponovo dodati
            Show(_tip);
        }
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        if (Menu == null) return;
        SetForegroundWindow(_src.Handle);
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
        // da se meni zatvori kad se klikne bilo gde van njega
        if (PresentationSource.FromVisual(Menu) is HwndSource ps) SetForegroundWindow(ps.Handle);
    }

    public void Dispose()
    {
        Remove();
        if (_ownIcon) DestroyIcon(_icon);
        _src.RemoveHook(Hook);
        _src.Dispose();
    }
}
